using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp.Content;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.Exports;

/// <summary>
/// #496: real EF (SQLite) coverage that <see cref="ExportTemplateAppService.ExportAsync"/> honours the two
/// filters the operator document list can express — the review queue (<c>HasReviewReasons</c>, #284 / #395) and
/// sub-document provenance (<c>OriginDocumentId</c>, #354) — so "export current view" exports exactly the view
/// rather than a broader set. Also pins the two ways that guarantee could silently rot: the review filter must
/// run the canonical <c>DocumentReviewQueries.RequiresAttention</c> predicate (a rejected document has already
/// been handled and must stay out), and the explicit <c>DocumentIds</c> branch must keep ignoring both filters.
/// </summary>
public class ExportTemplateAppService_ViewFilter_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private const string TypeCode = "invoice.general";

    private readonly IExportTemplateAppService _exportAppService;
    private readonly IExportTemplateRepository _templateRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICurrentTenant _currentTenant;

    public ExportTemplateAppService_ViewFilter_Tests()
    {
        _exportAppService = GetRequiredService<IExportTemplateAppService>();
        _templateRepository = GetRequiredService<IExportTemplateRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task Export_narrows_to_documents_requiring_review()
    {
        var templateId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            await SeedDocumentAsync(200m);
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            HasReviewReasons = true,
        });

        // Only the document carrying an unresolved review reason is exported.
        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    [Fact]
    public async Task Export_review_filter_excludes_a_rejected_document()
    {
        var templateId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            // Same unresolved reason, but the operator already rejected it — RequiresAttention drops it from the
            // queue. A hand-rolled `ReviewReasons != None` predicate would wrongly export this row, which is the
            // whole reason the export reuses DocumentReviewQueries.RequiresAttention instead of re-deriving it.
            await SeedDocumentAsync(200m, d =>
            {
                d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true);
                d.RejectReview("not a real invoice");
            });
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            HasReviewReasons = true,
        });

        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    [Fact]
    public async Task Export_narrows_to_the_sub_documents_of_a_source()
    {
        var templateId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, id: containerId);
            await SeedDerivedDocumentAsync(200m, containerId, "seg-1");
            await SeedDerivedDocumentAsync(300m, containerId, "seg-2");
            // An unrelated top-level document of the same type: must not ride along.
            await SeedDocumentAsync(400m);
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            OriginDocumentId = containerId,
        });

        // Exactly the container's children — not the container itself, not the unrelated document.
        csv.ShouldContain("200");
        csv.ShouldContain("300");
        csv.ShouldNotContain("100");
        csv.ShouldNotContain("400");
    }

    [Fact]
    public async Task Export_and_combines_the_review_filter_with_a_field_filter()
    {
        var templateId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            // Needs review, but the field filter excludes it.
            await SeedDocumentAsync(200m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            // Matches the field filter, but needs no review.
            await SeedDocumentAsync(300m);
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            HasReviewReasons = true,
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } },
        });

        // AND, not OR: only the row satisfying both survives.
        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
        csv.ShouldNotContain("300");
    }

    [Fact]
    public async Task Export_by_document_ids_ignores_the_view_filters()
    {
        var templateId = Guid.NewGuid();
        var pickedId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            // Needs no review and has no origin, yet it is explicitly picked: the ID set wins, exactly as it
            // already does for FieldFilters. This is the checked-export contract, not an accident.
            await SeedDocumentAsync(100m, id: pickedId);
            await SeedDocumentAsync(200m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            DocumentIds = new List<Guid> { pickedId },
            HasReviewReasons = true,
            OriginDocumentId = Guid.NewGuid(),
        });

        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    private async Task<string> ExportCsvAsync(ExportDocumentsInput input)
    {
        var content = await _exportAppService.ExportAsync(input);
        using var reader = new StreamReader(content.GetStream());
        return await reader.ReadToEndAsync();
    }

    private async Task SeedSchemaAsync()
    {
        await _documentTypeRepository.InsertAsync(
            new DocumentType(TypeId, _currentTenant.Id, TypeCode, TypeCode), autoSave: true);
        await _fieldDefinitionRepository.InsertAsync(
            new FieldDefinition(
                AmountFieldId, _currentTenant.Id, TypeId,
                name: "amount", displayName: "Amount", prompt: null, dataType: FieldDataType.Number),
            autoSave: true);
    }

    private Task SeedDocumentAsync(decimal amount, Action<Document>? configure = null, Guid? id = null)
    {
        var documentId = id ?? Guid.NewGuid();
        var doc = new Document(documentId, _currentTenant.Id, NewFileOrigin(documentId));
        return PersistAsync(doc, amount, configure);
    }

    private Task SeedDerivedDocumentAsync(decimal amount, Guid originDocumentId, string constituentKey)
    {
        var documentId = Guid.NewGuid();
        // A sub-document has no file of its own (#487 reverted FileOrigin to nullable); it is a Markdown slice
        // reached through OriginDocumentId.
        var doc = Document.CreateDerived(documentId, _currentTenant.Id, fileOrigin: null, originDocumentId, constituentKey);
        return PersistAsync(doc, amount, configure: null);
    }

    private async Task PersistAsync(Document doc, decimal amount, Action<Document>? configure)
    {
        // DocumentTypeId has a Domain private setter; simulate the classified state (#207 internal relation by Id).
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId);
        doc.SetFields(new[]
        {
            new DocumentFieldValue(AmountFieldId, FieldDataType.Number, JsonSerializer.SerializeToElement(amount)),
        });

        configure?.Invoke(doc);

        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    private async Task SeedTemplateAsync(Guid templateId)
    {
        await _templateRepository.InsertAsync(
            new ExportTemplate(
                templateId, _currentTenant.Id, "invoices", ExportFormat.Csv, TypeId,
                new List<ExportColumn> { new(AmountFieldId, 0) }),
            autoSave: true);
    }

    private static FileOrigin NewFileOrigin(Guid documentId) => new(
        blobName: $"blobs/{documentId:N}.pdf",
        uploadedByUserName: "test-user",
        contentType: "application/pdf",
        contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        fileSize: 1024,
        originalFileName: "invoice.pdf");

    private static Guid TypeId => DeterministicGuid("type:" + TypeCode);
    private static Guid AmountFieldId => DeterministicGuid("field:amount");
    private static Guid DeterministicGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
