using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Boolean;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class FieldExtractionCascadeTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());

        // FieldExtractionWorkflow is a concrete class, so use ForPartsOf with fake constructor dependencies.
        // Each test case configures the virtual ExtractAsync with Returns / Throws.
        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(),
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())),
            TestFieldTypeRegistry.Default);
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// Tests for the classification → field-extraction cascade engine (<see cref="FieldExtractionService"/>, invoked as
/// the cascade does, with the just-assigned TypeCode forwarded as the stale-reclassify hint): reclassify-race discard,
/// cross-tenant defense, MissingRequiredFields materialization, and #411
/// duplicate-fingerprint detection. Since #527 §8 the classification stage schedules this run <b>transactionally</b>
/// (before classification can derive Ready) rather than through a delayed <c>DocumentClassifiedEto</c> handler; the
/// scheduling itself is covered by the classification-job / app-service tests.
/// Tests derive stable Guids from name / code to keep mocks consistent.
/// </summary>
public class FieldExtractionCascade_Tests
    : VaultExtractApplicationTestBase<FieldExtractionCascadeTestModule>
{
    private readonly FieldExtractionService _service;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly FieldExtractionWorkflow _workflow;

    public FieldExtractionCascade_Tests()
    {
        _service = GetRequiredService<FieldExtractionService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
    }

    // ─── engine: field extraction behavior (invoked as the cascade does) ──────

    [Fact]
    public async Task No_Field_Definitions_Clears_And_Skips_The_Llm_Call()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>());

        var result = await Extract(doc.Id, null, "contract.general");

        // Cleared even with no field definitions, so downstream DocumentReady can still advance via the lifecycle round-trip.
        result.Outcome.ShouldBe(FieldExtractionOutcome.Cleared);

        // LLM should not be called; no field definitions short-circuit directly.
        await _workflow.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Field_Definitions_Clears_Stale_Fields_From_Previous_Type()
    {
        // Reclassifying to a type with no field definitions must clear stale field rows from the old schema (#206).
        var doc = CreateDocument(tenantId: null, typeCode: "blank.type");
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 100m });
        doc.FlexFields.ShouldNotBeEmpty();

        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>());

        await Extract(doc.Id, null, "blank.type");

        doc.FlexFields.ShouldBeEmpty();
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_Document_Logs_And_Returns_Skipped()
    {
        var docId = Guid.NewGuid();
        SetupType("contract.general");
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("contract.general", "amount") });
        _documentRepository.FindAsync(docId, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((Document?)null);

        var result = await Extract(docId, null, "contract.general");

        result.Outcome.ShouldBe(FieldExtractionOutcome.Skipped);
    }

    [Fact]
    public async Task Cross_Tenant_Event_Is_Discarded_Without_Writing_Fields()
    {
        // CLAUDE.md security covenant: requested tenant and Document.TenantId mismatch -> discard, defending against
        // DataFilter-disable paths.
        var eventTenant = Guid.NewGuid();
        var docTenant = Guid.NewGuid();
        var doc = CreateDocument(tenantId: docTenant, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("contract.general", "amount", tenantId: eventTenant) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement }));

        await Extract(doc.Id, eventTenant, "contract.general");

        doc.FlexFields.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stale_TypeCode_From_Reclassify_Race_Is_Discarded()
    {
        // Reclassify race: the event carries contract.general but the document was reclassified to invoice.general.
        // Continuing would write contract-schema values under the invoice type; the stale event must be discarded.
        var doc = CreateDocument(tenantId: null, typeCode: "invoice.general");
        SetupType("contract.general");
        SetupType("invoice.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("contract.general", "amount") });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement }));

        await Extract(doc.Id, null, "contract.general"); // stale typeCode resolves to a different typeId than the doc

        doc.FlexFields.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Happy_Path_Writes_Fields()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);

        var defs = new List<Field>
        {
            CreateFieldDefinition("contract.general", "amount", NumberFieldType.ControlName),
            CreateFieldDefinition("contract.general", "party", TextFieldType.ControlName),
            CreateFieldDefinition("contract.general", "date", DateTimeFieldType.ControlName)
        };
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>()).Returns(defs);
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement,
                ["party"] = JsonDocument.Parse("\"Acme Corp\"").RootElement,
                ["date"] = null // LLM failed to extract it, so it should not enter the field set.
            }));

        await Extract(doc.Id, null, "contract.general");

        // v3 keys the bag by field name; a field the LLM could not extract contributes no entry at all,
        // which is what keeps FieldCount at 2.
        doc.FlexFields.Keys.ShouldBe(new[] { "amount", "party" }, ignoreOrder: true);

        await _documentRepository.Received(1).UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renamed_TypeCode_Event_Uses_Current_DocumentTypeId()
    {
        // TypeCode rename race: the event carries the old code but DocumentTypeId is the stable relation. When the old
        // code is unresolvable, extraction proceeds against the current type Id.
        var typeId = TypeId("contract.general");
        var doc = CreateDocument(tenantId: null, documentTypeId: typeId);
        SetupType("contract.renamed", typeId: typeId);
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition(typeId, "amount", NumberFieldType.ControlName) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await Extract(doc.Id, null, "contract.general"); // old, now-unresolvable code

        doc.FlexFields.Keys.ShouldBe(new[] { "amount" });
    }

    [Fact]
    public async Task DataType_Changed_During_Extraction_Skips_Stale_Value()
    {
        // While the LLM call is in flight an admin changes the field type Number -> Text; the number extracted from the
        // old descriptor must not be written into the current text field.
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);

        var initialDefs = new List<Field> { CreateFieldDefinition("contract.general", "amount", NumberFieldType.ControlName) };
        var currentDefs = new List<Field> { CreateFieldDefinition("contract.general", "amount", TextFieldType.ControlName) };
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(initialDefs, currentDefs);
        _workflow.ExtractAsync(
                Arg.Is<IReadOnlyList<FieldExtractionDescriptor>>(d => d.Count == 1 && d[0].Name == "amount" && d[0].FieldTypeName == NumberFieldType.ControlName),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await Extract(doc.Id, null, "contract.general");

        doc.FlexFields.ShouldBeEmpty();
        await _documentRepository.Received(1).UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_Required_Field_Sets_MissingRequiredFields_Reason()
    {
        // #284: a required field was not extracted -> materialize MissingRequiredFields (non-blocking, operator queue).
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>
            {
                CreateFieldDefinition("contract.general", "amount", NumberFieldType.ControlName, isRequired: true),
                CreateFieldDefinition("contract.general", "party", TextFieldType.ControlName)
            });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?>
            {
                ["amount"] = null, // required value missing
                ["party"] = JsonDocument.Parse("\"Acme\"").RootElement
            }));

        await Extract(doc.Id, null, "contract.general");

        (doc.ReviewReasons & DocumentReviewReasons.MissingRequiredFields)
            .ShouldBe(DocumentReviewReasons.MissingRequiredFields);
    }

    [Fact]
    public async Task All_Required_Fields_Present_Does_Not_Set_MissingRequiredFields()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("contract.general", "amount", NumberFieldType.ControlName, isRequired: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await Extract(doc.Id, null, "contract.general");

        (doc.ReviewReasons & DocumentReviewReasons.MissingRequiredFields).ShouldBe(DocumentReviewReasons.None);
    }

    // ─── #411 duplicate-fingerprint detection ────────────────────────────────

    [Fact]
    public async Task Duplicate_Fingerprint_Collision_Sets_DuplicateSuspected_And_Stores_Fingerprint()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("receipt.general", "receipt_no", TextFieldType.ControlName, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement }));
        // A colliding document exists in the same layer + type.
        _documentRepository.FindDuplicateCandidatesAsync(
                doc.Id, TypeId("receipt.general"), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<DuplicateCandidateModel> { new() { Id = Guid.NewGuid(), Title = "Existing receipt" } });

        await Extract(doc.Id, null, "receipt.general");

        doc.FieldFingerprint.ShouldNotBeNull();
        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.DuplicateSuspected);
    }

    [Fact]
    public async Task No_Fingerprint_Collision_Does_Not_Set_DuplicateSuspected()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("receipt.general", "receipt_no", TextFieldType.ControlName, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement }));
        _documentRepository.FindDuplicateCandidatesAsync(
                doc.Id, TypeId("receipt.general"), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<DuplicateCandidateModel>());

        await Extract(doc.Id, null, "receipt.general");

        doc.FieldFingerprint.ShouldNotBeNull();
        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
    }

    /// <summary>
    /// #411's override survives routine re-extraction, and #651 §6 is what makes that statement precise: it
    /// survives re-extraction <b>onto the same key</b>. The operator can only have allowed a document that was
    /// already flagged, so the realistic shape is two passes — extract, allow, extract again — not an override
    /// pinned on a document that never had a key. (Pinning it that way is now a contradiction the aggregate
    /// resolves against the override, which is the point of the sibling test below.)
    /// </summary>
    [Fact]
    public async Task DuplicateAllowed_Override_Survives_ReExtraction_And_Skips_The_Collision_Query()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("receipt.general", "receipt_no", TextFieldType.ControlName, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement }));
        _documentRepository.FindDuplicateCandidatesAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<DuplicateCandidateModel> { new() { Id = Guid.NewGuid(), Title = "Existing receipt" } });

        // First pass raises the flag; the operator reviews it and decides this is not a duplicate.
        await Extract(doc.Id, null, "receipt.general");
        var firstKey = doc.FieldFingerprint;
        firstKey.ShouldNotBeNull();
        doc.AllowDuplicate();
        _documentRepository.ClearReceivedCalls();

        // Second pass reproduces exactly the same key.
        await Extract(doc.Id, null, "receipt.general");

        doc.FieldFingerprint.ShouldBe(firstKey);
        doc.DuplicateAllowed.ShouldBeTrue();
        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
        // The override short-circuits the collision query entirely.
        await _documentRepository.DidNotReceive().FindDuplicateCandidatesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Partial_Unique_Key_Yields_No_Fingerprint_And_No_Collision_Query()
    {
        // Two unique-key fields but only one extracted -> partial key -> no fingerprint, no duplicate check.
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>
            {
                CreateFieldDefinition("receipt.general", "receipt_no", TextFieldType.ControlName, isUniqueKey: true),
                CreateFieldDefinition("receipt.general", "amount", NumberFieldType.ControlName, isUniqueKey: true)
            });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?>
            {
                ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement,
                ["amount"] = null // missing -> partial key
            }));

        await Extract(doc.Id, null, "receipt.general");

        doc.FieldFingerprint.ShouldBeNull();
        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
        await _documentRepository.DidNotReceive().FindDuplicateCandidatesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #651 §6: the override is not unconditional. Re-extraction that lands on a <b>different</b> key withdraws
    /// it — the operator's "not a duplicate" verdict was about the values they looked at — so the new key's
    /// collision is raised rather than suppressed. The rule lives on <c>Document.SetFieldFingerprint</c>, so this
    /// holds for the pipeline without the pipeline doing anything about it.
    /// </summary>
    [Fact]
    public async Task ReExtraction_Onto_A_Different_Key_Withdraws_DuplicateAllowed_And_ReFlags()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        // The operator reviewed the OLD key (R-001) and cleared it.
        doc.SetFieldFingerprint("old-key-fingerprint");
        doc.AllowDuplicate();
        doc.DuplicateAllowed.ShouldBeTrue();

        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("receipt.general", "receipt_no", TextFieldType.ControlName, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-002\"").RootElement }));
        _documentRepository.FindDuplicateCandidatesAsync(
                doc.Id, TypeId("receipt.general"), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<DuplicateCandidateModel> { new() { Id = Guid.NewGuid(), Title = "The real R-002" } });

        await Extract(doc.Id, null, "receipt.general");

        doc.DuplicateAllowed.ShouldBeFalse();
        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.DuplicateSuspected);
    }

    // ─── #527 §5/§7: field validation warning persistence ───────────────────

    [Fact]
    public async Task Warning_Is_Persisted_Alongside_The_Kept_Value_And_Sets_The_Bit()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "bank.statement");
        SetupType("bank.statement");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("bank.statement"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("bank.statement", "transactions", TextFieldType.ControlName) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResultWith(
                new Dictionary<string, JsonElement?> { ["transactions"] = JsonDocument.Parse("\"| a | b |\"").RootElement },
                new FieldValidationWarningResult("transactions", "Row 4 balance does not reconcile.")));

        await Extract(doc.Id, null, "bank.statement");

        // The value is kept (a warning never nulls the value)...
        doc.FlexFields.Keys.ShouldContain("transactions");
        // ...and the warning is persisted for the resolved FieldDefinitionId, with the blocking bit set.
        doc.FieldValidationWarnings.Count.ShouldBe(1);
        doc.FieldValidationWarnings.Single().FieldDefinitionId.ShouldBe(FieldId("transactions"));
        doc.FieldValidationWarnings.Single().Message.ShouldBe("Row 4 balance does not reconcile.");
        (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning).ShouldBe(DocumentReviewReasons.FieldValidationWarning);
    }

    [Fact]
    public async Task Clean_ReExtraction_Replaces_And_Clears_A_Prior_Warning()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "bank.statement");
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(FieldId("transactions"), "old mismatch") });
        (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning).ShouldBe(DocumentReviewReasons.FieldValidationWarning);
        SetupType("bank.statement");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("bank.statement"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { CreateFieldDefinition("bank.statement", "transactions", TextFieldType.ControlName) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?>
            {
                ["transactions"] = JsonDocument.Parse("\"| a | b |\"").RootElement
            }));

        await Extract(doc.Id, null, "bank.statement");

        doc.FieldValidationWarnings.ShouldBeEmpty();
        (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning).ShouldBe(DocumentReviewReasons.None);
    }

    [Fact]
    public async Task Warning_For_Field_Whose_Shape_Changed_MidFlight_Is_Discarded()
    {
        // Mirrors the value in-flight guard: while the LLM was in flight the field's DataType changed, so both the value
        // and its warning are stale and discarded (§7).
        var doc = CreateDocument(tenantId: null, typeCode: "bank.statement");
        SetupType("bank.statement");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        var initialDefs = new List<Field> { CreateFieldDefinition("bank.statement", "amount", NumberFieldType.ControlName) };
        var currentDefs = new List<Field> { CreateFieldDefinition("bank.statement", "amount", TextFieldType.ControlName) };
        _fieldRepository.GetListAsync(TypeId("bank.statement"), Arg.Any<CancellationToken>())
            .Returns(initialDefs, currentDefs);
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResultWith(
                new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement },
                new FieldValidationWarningResult("amount", "some rule failed")));

        await Extract(doc.Id, null, "bank.statement");

        doc.FieldValidationWarnings.ShouldBeEmpty();
        (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning).ShouldBe(DocumentReviewReasons.None);
    }

    [Fact]
    public async Task No_Field_Definitions_Clears_Stale_Validation_Warnings()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "blank.type");
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(FieldId("amount"), "old") });
        (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning).ShouldBe(DocumentReviewReasons.FieldValidationWarning);
        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<Field>());

        await Extract(doc.Id, null, "blank.type");

        doc.FieldValidationWarnings.ShouldBeEmpty();
        (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning).ShouldBe(DocumentReviewReasons.None);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static FieldExtractionWorkflowResult WorkflowResult(Dictionary<string, JsonElement?> values) =>
        new(values, Array.Empty<FieldValidationWarningResult>());

    private static FieldExtractionWorkflowResult WorkflowResultWith(
        Dictionary<string, JsonElement?> values, params FieldValidationWarningResult[] warnings) =>
        new(values, warnings);

    private Task<FieldExtractionResult> Extract(Guid documentId, Guid? tenantId, string eventTypeCode)
        => _service.ExtractAsync(documentId, tenantId, expectedEventTypeCode: eventTypeCode);

    private void SetupType(string code, Guid? tenantId = null, Guid? typeId = null)
    {
        var id = typeId ?? TypeId(code);
        var type = new DocumentType(id, tenantId, code, code);
        _documentTypeRepository.FindByTypeCodeAsync(code, Arg.Any<CancellationToken>()).Returns(type);
        _documentTypeRepository.FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
    }

    private static Document CreateDocument(Guid? tenantId, string typeCode)
        => CreateDocument(tenantId, TypeId(typeCode));

    private static Document CreateDocument(Guid? tenantId, Guid documentTypeId)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        // Application.Tests has no InternalsVisibleTo; write the classified state through the internal channel.
        Invoke(doc, "ApplyAutomaticClassificationResult", documentTypeId, 0.99);
        Invoke(doc, "SetMarkdown", "# Body");
        return doc;
    }

    private static void Invoke(Document doc, string method, params object[] args) =>
        typeof(Document)
            .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, args);

    private static Field CreateFieldDefinition(
        string documentTypeCode, string name, string fieldTypeName = TextFieldType.ControlName,
        Guid? tenantId = null, bool isRequired = false, bool isUniqueKey = false) =>
        CreateFieldDefinition(TypeId(documentTypeCode), name, fieldTypeName, tenantId, isRequired, isUniqueKey);

    private static Field CreateFieldDefinition(
        Guid documentTypeId, string name, string fieldTypeName = TextFieldType.ControlName,
        Guid? tenantId = null, bool isRequired = false, bool isUniqueKey = false) =>
        new(
            id: FieldId(name),
            tenantId: tenantId,
            documentTypeId: documentTypeId,
            name: name,
            displayName: name,
            fieldTypeName: fieldTypeName,
            description: $"Extract the {name}.",
            displayOrder: 0,
            isRequired: isRequired,
            isUniqueKey: isUniqueKey);

    private static Guid TypeId(string code) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + code)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}
