using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Boolean;
using Dignite.Abp.FlexFields.CKEditor;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Select;
using Dignite.Abp.FlexFields.Table;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.FlexFields.Tags;
using Microsoft.Extensions.Hosting;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Host.Data;

/// <summary>
/// Dev/demo-only seed (#627): a Host-layer "Sample Document" document type carrying one field per
/// currently-registered field type (Text / Number / Boolean / DateTime / Select / CKEditor / Tags /
/// Table), plus one fully "processed" sample <see cref="Document"/> with a populated value for every
/// field, reaching a Ready-gate-passing state — so the field-designer UI, extraction pipeline, and
/// export/egress paths have something to look at without hand-creating a type through the admin UI first.
/// <para>
/// This is deliberately <b>not</b> the "no built-in document types" module guarantee CLAUDE.md documents
/// for <c>core/</c> — that guarantee is about the reusable module never auto-creating a type for every
/// consumer. This class lives entirely under <c>host/</c>, runs only when
/// <see cref="IHostEnvironment.IsDevelopment"/> is true, and never touches a tenant layer (TenantId stays
/// <c>null</c> throughout — Host layer only, #627 decision 3).
/// </para>
/// <para>
/// No real OCR / classification / field-extraction / LLM call runs here. The "already processed" state is
/// reached the same way this repo's own EF Core tests reach it for fixtures
/// (<c>DocumentTestData.MarkClassified</c>) — by driving the aggregate through its own domain-service
/// surface rather than the real pipeline. <see cref="Document.SetMarkdown"/> /
/// <see cref="Document.ConfirmClassification"/> etc. are <c>internal</c> to
/// <c>Dignite.Vault.Extract.Domain</c> and are not visible from this Host assembly (unlike
/// <c>Dignite.Vault.Extract.EntityFrameworkCore.Tests</c>, the one project the Domain project's
/// <c>InternalsVisibleTo</c> names) — <see cref="DocumentPipelineRunManager"/> is the public,
/// cross-assembly surface the real pipeline itself calls through, so this seed drives exactly that, never
/// reflection and never a second internals-visibility grant.
/// </para>
/// <para>
/// Idempotent like <see cref="VaultExtractHostRoleDataSeedContributor"/>: the document type is looked up
/// by its fixed <see cref="SampleTypeCode"/>, each field is looked up by name under that type, and the
/// sample document is looked up by a fixed well-known <see cref="SampleDocumentId"/> before anything is
/// created — re-running the seed (DbMigrator re-run, container restart) never duplicates data or throws on
/// a unique-name conflict.
/// </para>
/// </summary>
public class VaultExtractSampleDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    /// <summary>Stable identifier for the seeded Host-layer document type; also the idempotency key.</summary>
    public const string SampleTypeCode = "sample-document";

    /// <summary>Stable, well-known id for the seeded sample document; also the idempotency key.</summary>
    private static readonly Guid SampleDocumentId = Guid.Parse("9d1f6b9e-9d9e-4c9a-8d1f-6b9e9d9e4c9a");

    private const string SampleMarkdown =
        "# Sample Service Agreement\n\n" +
        "This is a dev-only sample document seeded for local development and manual QA of the field " +
        "designer UI, extraction pipeline, and export/egress paths (#627). It exercises all eight " +
        "currently-registered field types with populated demo values.\n\n" +
        "## Summary\n\n" +
        "The agreement below is fictitious and for demonstration purposes only. It illustrates a document " +
        "that has already completed OCR, classification, and field extraction, and is ready for export.";

    private readonly IHostEnvironment _hostEnvironment;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentTypeManager _documentTypeManager;
    private readonly FieldDefinitionManager _fieldDefinitionManager;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IFlexFieldIndexManager<Document> _flexFieldIndexManager;

    public VaultExtractSampleDataSeedContributor(
        IHostEnvironment hostEnvironment,
        ICurrentTenant currentTenant,
        IDocumentTypeRepository documentTypeRepository,
        IFieldRepository fieldRepository,
        IDocumentRepository documentRepository,
        DocumentTypeManager documentTypeManager,
        FieldDefinitionManager fieldDefinitionManager,
        DocumentPipelineRunManager pipelineRunManager,
        IFlexFieldIndexManager<Document> flexFieldIndexManager)
    {
        _hostEnvironment = hostEnvironment;
        _currentTenant = currentTenant;
        _documentTypeRepository = documentTypeRepository;
        _fieldRepository = fieldRepository;
        _documentRepository = documentRepository;
        _documentTypeManager = documentTypeManager;
        _fieldDefinitionManager = fieldDefinitionManager;
        _pipelineRunManager = pipelineRunManager;
        _flexFieldIndexManager = flexFieldIndexManager;
    }

    [UnitOfWork]
    public virtual async Task SeedAsync(DataSeedContext context)
    {
        // Safety gate (#627): a dev/demo convenience only, never intended to run against a real deployment.
        if (!_hostEnvironment.IsDevelopment())
        {
            return;
        }

        // Host layer only (#627 decision 3) — actually enforced, not just documented. A tenant-scoped
        // seed (ABP's TenantAppService.CreateAsync runs every IDataSeedContributor, this one included,
        // under CurrentTenant.Change(tenant.Id)) used to fall through to the block below: the
        // tenant-filtered FindAsync(SampleDocumentId) can never see the Host-layer row, so the seed
        // inserted a second Document under the same fixed SampleDocumentId — a primary-key collision, or
        // worse, silent duplication for the type/fields lookups above it (#640). Returning early keeps
        // this contributor a Host-only fixture in fact, not only in comment.
        if (context?.TenantId != null)
        {
            return;
        }

        // Explicit Host scope, not merely "context carried no TenantId": this must not depend on every
        // caller happening to pass a Host-scoped context.
        using (_currentTenant.Change(null))
        {
            var documentType = await GetOrCreateSampleDocumentTypeAsync();
            var definitions = BuildFieldDefinitions();
            await GetOrCreateSampleFieldsAsync(documentType, definitions);
            await GetOrCreateSampleDocumentAsync(documentType, definitions);
        }
    }

    private async Task<DocumentType> GetOrCreateSampleDocumentTypeAsync()
    {
        var existing = await _documentTypeRepository.FindByTypeCodeAsync(SampleTypeCode);
        if (existing != null)
        {
            return existing;
        }

        // Mirrors DocumentTypeAppService.CreateAsync (minus its [Authorize] gate, which a data-seed
        // context — no authenticated principal — cannot pass; VaultExtractHostRoleDataSeedContributor
        // takes the same domain-manager route for the same reason).
        await _documentTypeManager.CheckCodeAvailableAsync(SampleTypeCode);

        var documentType = new DocumentType(
            Guid.NewGuid(),
            tenantId: null,
            typeCode: SampleTypeCode,
            displayName: "Sample Document (Seed Data)",
            description: "Dev-only seed document type (#627) exercising every registered field type. Safe to delete.",
            confidenceThreshold: 0.7,
            priority: 0);

        await _documentTypeRepository.InsertAsync(documentType, autoSave: true);
        return documentType;
    }

    private async Task GetOrCreateSampleFieldsAsync(
        DocumentType documentType, IReadOnlyList<FieldSeedDefinition> definitions)
    {
        var order = 0;
        foreach (var definition in definitions)
        {
            var existing = await _fieldRepository.FindByNameAsync(documentType.Id, definition.Name);
            if (existing == null)
            {
                // Mirrors FieldDefinitionAppService.CreateAsync (minus its [Authorize] gate — same reason
                // as the document type above).
                await _fieldDefinitionManager.CheckNameAvailableAsync(documentType.Id, definition.Name);

                var field = new Field(
                    Guid.NewGuid(),
                    tenantId: null,
                    documentTypeId: documentType.Id,
                    name: definition.Name,
                    displayName: definition.DisplayName,
                    fieldTypeName: definition.FieldTypeName,
                    description: definition.Description,
                    configuration: definition.Configuration,
                    displayOrder: order,
                    isRequired: false,
                    isSearchable: definition.IsSearchable,
                    isUniqueKey: false);

                await _fieldRepository.InsertAsync(field, autoSave: true);
            }

            order++;
        }
    }

    private async Task GetOrCreateSampleDocumentAsync(
        DocumentType documentType, IReadOnlyList<FieldSeedDefinition> definitions)
    {
        var existing = await _documentRepository.FindAsync(SampleDocumentId, includeDetails: false);
        if (existing != null)
        {
            return;
        }

        // Fictitious blob reference: this seed never writes a blob, so "download original" would 404 for
        // this one document — an acceptable rough edge for a dev/demo fixture, not a functional path this
        // seed needs to support.
        var fileOrigin = new FileOrigin(
            blobName: "seed/sample-document.pdf",
            uploadedByUserName: "Seed",
            contentType: "application/pdf",
            contentHash: new string('0', 64),
            fileSize: 24_576,
            originalFileName: "sample-service-agreement.pdf");

        var document = new Document(SampleDocumentId, tenantId: null, fileOrigin);
        await _documentRepository.InsertAsync(document, autoSave: true);

        // Parse: writes Markdown + Title (write-once) and completes the key pipeline run.
        var parseRun = await _pipelineRunManager.StartAsync(document, VaultExtractPipelines.Parse);
        await _pipelineRunManager.CompleteParseAsync(
            document,
            parseRun,
            markdown: SampleMarkdown,
            title: "Sample Service Agreement (Seed Data)",
            language: "en");

        // Classification: the "operator confirmed" path — sets DocumentTypeId, pins
        // ClassificationConfidence to 1.0, and marks ReviewDisposition Confirmed, clearing
        // UnresolvedClassification / MissingRequiredFields along the way.
        var classificationRun = await _pipelineRunManager.StartAsync(document, VaultExtractPipelines.Classification);
        await _pipelineRunManager.CompleteManualClassificationAsync(document, classificationRun, documentType);

        // Field extraction: SetFlexFields + the index sync it owes in the same unit of work
        // (.claude/rules/field-architecture.md — miss this and the document stops matching its own field
        // filters, silently), then complete the run so the key-pipeline set is all Succeeded.
        var fieldExtractionRun = await _pipelineRunManager.StartAsync(document, VaultExtractPipelines.FieldExtraction);
        document.SetFlexFields(BuildSampleFieldValues(definitions));
        await _flexFieldIndexManager.SynchronizeAsync(document);
        await _pipelineRunManager.CompleteAsync(document, fieldExtractionRun);

        await _documentRepository.UpdateAsync(document, autoSave: true);
    }

    /// <summary>
    /// One definition per currently-registered field type (#562 / #625's eight: Text, Number, Boolean,
    /// DateTime, Select, CKEditor, Tags, Table), each with a configuration realistic enough to be useful as
    /// a demo rather than a bare default. <c>IsSearchable</c> is <c>false</c> for CKEditor and Table:
    /// neither has a query-index slot (<c>IFieldType.IndexValueType</c> is null for both,
    /// <c>FieldTypeCatalogAndSearchability_Tests</c>), and <c>FieldDefinitionAppService.CreateAsync</c>'s
    /// own <c>CheckSearchable</c> guard would reject <c>true</c> here on the real path this seed mirrors.
    /// </summary>
    private static List<FieldSeedDefinition> BuildFieldDefinitions() => new()
    {
        new FieldSeedDefinition(
            "reference_number",
            "Reference Number",
            TextFieldType.ControlName,
            "The document's internal reference or file number.",
            new TextConfiguration { Mode = TextMode.SingleLine, CharLimit = 64 }.ConfigurationDictionary,
            IsSearchable: true,
            Value: "DOC-2026-00042"),

        new FieldSeedDefinition(
            "total_amount",
            "Total Amount",
            NumberFieldType.ControlName,
            "The total monetary amount stated in the document.",
            new NumberConfiguration { Decimals = 2 }.ConfigurationDictionary,
            IsSearchable: true,
            Value: 12500.00m),

        new FieldSeedDefinition(
            "is_confidential",
            "Is Confidential",
            BooleanFieldType.ControlName,
            "Whether the document is marked confidential.",
            Configuration: null,
            IsSearchable: true,
            Value: true),

        new FieldSeedDefinition(
            "effective_date",
            "Effective Date",
            DateTimeFieldType.ControlName,
            "The date the agreement takes effect.",
            new DateTimeConfiguration { InputMode = DateTimeInputMode.Date }.ConfigurationDictionary,
            IsSearchable: true,
            Value: new DateTime(2026, 1, 15)),

        new FieldSeedDefinition(
            "status",
            "Status",
            SelectFieldType.ControlName,
            "The document's current approval status.",
            new SelectConfiguration
            {
                Options = new List<SelectListItem>
                {
                    new("Draft", "draft", false),
                    new("In Review", "in_review", false),
                    new("Approved", "approved", false),
                    new("Rejected", "rejected", false)
                }
            }.ConfigurationDictionary,
            IsSearchable: true,
            Value: "approved"),

        new FieldSeedDefinition(
            "notes",
            "Notes",
            CKEditorFieldType.ControlName,
            "Free-form reviewer notes.",
            new CKEditorConfiguration
            {
                ContentFormat = CKEditorContentFormat.Markdown,
                Mode = CKEditorMode.Basic
            }.ConfigurationDictionary,
            IsSearchable: false,
            Value: "**Reviewed** by the operations team.\n\n- No open issues\n- Ready for archival"),

        new FieldSeedDefinition(
            "tags",
            "Tags",
            TagsFieldType.ControlName,
            "Free-form labels for search and filtering.",
            Configuration: null,
            IsSearchable: true,
            Value: new List<string> { "sample", "demo", "seed-data" }),

        new FieldSeedDefinition(
            "line_items",
            "Line Items",
            TableFieldType.ControlName,
            "Line items covered by the agreement.",
            new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName },
                    new() { Name = "quantity", DisplayName = "Quantity", FieldTypeName = NumberFieldType.ControlName }
                }
            }.ConfigurationDictionary,
            IsSearchable: false,
            Value: new List<TableRow>
            {
                new() { Values = { ["item"] = "Consulting hours", ["quantity"] = 40m } },
                new() { Values = { ["item"] = "Support license", ["quantity"] = 1m } }
            })
    };

    private static Dictionary<string, object?> BuildSampleFieldValues(IReadOnlyList<FieldSeedDefinition> definitions)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            values[definition.Name] = definition.Value;
        }

        return values;
    }

    private sealed record FieldSeedDefinition(
        string Name,
        string DisplayName,
        string FieldTypeName,
        string? Description,
        FieldConfigurationDictionary? Configuration,
        bool IsSearchable,
        object Value);
}
