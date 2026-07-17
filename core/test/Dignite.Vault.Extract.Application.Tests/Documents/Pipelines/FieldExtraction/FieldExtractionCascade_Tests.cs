using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
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
using Volo.Abp.EventBus.Distributed;
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
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // FieldExtractionWorkflow is a concrete class, so use ForPartsOf with fake constructor dependencies.
        // Each test case configures the virtual ExtractAsync with Returns / Throws.
        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(),
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())));
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// Tests for the classification → field-extraction cascade engine (<see cref="FieldExtractionService"/>, invoked as
/// the cascade does, with the just-assigned TypeCode forwarded as the stale-reclassify hint): reclassify-race discard,
/// cross-tenant defense, FieldsExtractedEto contract, MissingRequiredFields materialization, and #411
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
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IDistributedEventBus _eventBus;

    public FieldExtractionCascade_Tests()
    {
        _service = GetRequiredService<FieldExtractionService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    // ─── engine: field extraction behavior (invoked as the cascade does) ──────

    [Fact]
    public async Task No_Field_Definitions_Publishes_Empty_FieldsExtractedEto()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        await Extract(doc.Id, null, "contract.general");

        // Publish an empty event even with no field definitions, so downstream DocumentReady can advance.
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id && e.DocumentTypeCode == "contract.general" && e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());

        // LLM should not be called; no field definitions short-circuit directly.
        await _workflow.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Field_Definitions_Clears_Stale_Fields_From_Previous_Type()
    {
        // Reclassifying to a type with no field definitions must clear stale field rows from the old schema (#206).
        var doc = CreateDocument(tenantId: null, typeCode: "blank.type");
        doc.SetFields(new[]
        {
            new DocumentFieldValue(FieldId("amount"), FieldDataType.Number, JsonDocument.Parse("100").RootElement)
        });
        doc.ExtractedFieldValues.ShouldNotBeEmpty();

        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        await Extract(doc.Id, null, "blank.type");

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Missing_Document_Logs_And_Returns_Without_Publishing()
    {
        var docId = Guid.NewGuid();
        SetupType("contract.general");
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount") });
        _documentRepository.FindAsync(docId, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((Document?)null);

        await Extract(docId, null, "contract.general");

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
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
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount", tenantId: eventTenant) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement }));

        await Extract(doc.Id, eventTenant, "contract.general");

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
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
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount") });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement }));

        await Extract(doc.Id, null, "contract.general"); // stale typeCode resolves to a different typeId than the doc

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Happy_Path_Writes_Fields_And_Publishes_FieldsExtractedEto()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);

        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Number),
            CreateFieldDefinition("contract.general", "party", FieldDataType.Text),
            CreateFieldDefinition("contract.general", "date", FieldDataType.Date)
        };
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>()).Returns(defs);
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement,
                ["party"] = JsonDocument.Parse("\"Acme Corp\"").RootElement,
                ["date"] = null // LLM failed to extract it, so it should not enter the field set.
            }));

        await Extract(doc.Id, null, "contract.general");

        var fieldIds = doc.ExtractedFieldValues.Select(f => f.FieldDefinitionId).ToList();
        fieldIds.Count.ShouldBe(2);
        fieldIds.ShouldContain(FieldId("amount"));
        fieldIds.ShouldContain(FieldId("party"));
        fieldIds.ShouldNotContain(FieldId("date"));

        await _documentRepository.Received(1).UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id && e.DocumentTypeCode == "contract.general" && e.FieldCount == 2),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Renamed_TypeCode_Event_Uses_Current_DocumentTypeId_And_Publishes_Current_Code()
    {
        // TypeCode rename race: the event carries the old code but DocumentTypeId is the stable relation. When the old
        // code is unresolvable, extraction proceeds against the current type Id and publishes the current TypeCode.
        var typeId = TypeId("contract.general");
        var doc = CreateDocument(tenantId: null, documentTypeId: typeId);
        SetupType("contract.renamed", typeId: typeId);
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition(typeId, "amount", FieldDataType.Number) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await Extract(doc.Id, null, "contract.general"); // old, now-unresolvable code

        doc.ExtractedFieldValues.Count.ShouldBe(1);
        doc.ExtractedFieldValues.Single().FieldDefinitionId.ShouldBe(FieldId("amount"));
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id && e.DocumentTypeCode == "contract.renamed" && e.FieldCount == 1),
            Arg.Any<bool>(), Arg.Any<bool>());
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

        var initialDefs = new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount", FieldDataType.Number) };
        var currentDefs = new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount", FieldDataType.Text) };
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(initialDefs, currentDefs);
        _workflow.ExtractAsync(
                Arg.Is<IReadOnlyList<FieldExtractionDescriptor>>(d => d.Count == 1 && d[0].Name == "amount" && d[0].DataType == FieldDataType.Number),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement }));

        await Extract(doc.Id, null, "contract.general");

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.Received(1).UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id && e.DocumentTypeCode == "contract.general" && e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Missing_Required_Field_Sets_MissingRequiredFields_Reason()
    {
        // #284: a required field was not extracted -> materialize MissingRequiredFields (non-blocking, operator queue).
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                CreateFieldDefinition("contract.general", "amount", FieldDataType.Number, isRequired: true),
                CreateFieldDefinition("contract.general", "party", FieldDataType.Text)
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
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount", FieldDataType.Number, isRequired: true) });
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
        _fieldDefinitionRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("receipt.general", "receipt_no", FieldDataType.Text, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement }));
        // A colliding document exists in the same layer + type.
        _documentRepository.FindDuplicateCandidatesAsync(
                doc.Id, TypeId("receipt.general"), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _fieldDefinitionRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("receipt.general", "receipt_no", FieldDataType.Text, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement }));
        _documentRepository.FindDuplicateCandidatesAsync(
                doc.Id, TypeId("receipt.general"), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<DuplicateCandidateModel>());

        await Extract(doc.Id, null, "receipt.general");

        doc.FieldFingerprint.ShouldNotBeNull();
        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
    }

    [Fact]
    public async Task DuplicateAllowed_Override_Suppresses_ReFlagging_And_Skips_Collision_Query()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        doc.AllowDuplicate(); // operator previously decided this is not a duplicate
        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("receipt.general", "receipt_no", FieldDataType.Text, isUniqueKey: true) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResult(new Dictionary<string, JsonElement?> { ["receipt_no"] = JsonDocument.Parse("\"R-001\"").RootElement }));

        await Extract(doc.Id, null, "receipt.general");

        (doc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
        // The override short-circuits the collision query entirely.
        await _documentRepository.DidNotReceive().FindDuplicateCandidatesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Partial_Unique_Key_Yields_No_Fingerprint_And_No_Collision_Query()
    {
        // Two unique-key fields but only one extracted -> partial key -> no fingerprint, no duplicate check.
        var doc = CreateDocument(tenantId: null, typeCode: "receipt.general");
        SetupType("receipt.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("receipt.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                CreateFieldDefinition("receipt.general", "receipt_no", FieldDataType.Text, isUniqueKey: true),
                CreateFieldDefinition("receipt.general", "amount", FieldDataType.Number, isUniqueKey: true)
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
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ─── #527 §5/§7: field validation warning persistence ───────────────────

    [Fact]
    public async Task Warning_Is_Persisted_Alongside_The_Kept_Value_And_Sets_The_Bit()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "bank.statement");
        SetupType("bank.statement");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("bank.statement"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("bank.statement", "transactions", FieldDataType.Text) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowResultWith(
                new Dictionary<string, JsonElement?> { ["transactions"] = JsonDocument.Parse("\"| a | b |\"").RootElement },
                new FieldValidationWarningResult("transactions", "Row 4 balance does not reconcile.")));

        await Extract(doc.Id, null, "bank.statement");

        // The value is kept (a warning never nulls the value)...
        doc.ExtractedFieldValues.Select(v => v.FieldDefinitionId).ShouldContain(FieldId("transactions"));
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
        _fieldDefinitionRepository.GetListAsync(TypeId("bank.statement"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("bank.statement", "transactions", FieldDataType.Text) });
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
        var initialDefs = new List<FieldDefinition> { CreateFieldDefinition("bank.statement", "amount", FieldDataType.Number) };
        var currentDefs = new List<FieldDefinition> { CreateFieldDefinition("bank.statement", "amount", FieldDataType.Text) };
        _fieldDefinitionRepository.GetListAsync(TypeId("bank.statement"), Arg.Any<CancellationToken>())
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
        _fieldDefinitionRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

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

    private Task Extract(Guid documentId, Guid? tenantId, string eventTypeCode)
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

    private static FieldDefinition CreateFieldDefinition(
        string documentTypeCode, string name, FieldDataType dataType = FieldDataType.Text,
        Guid? tenantId = null, bool isRequired = false, bool isUniqueKey = false) =>
        CreateFieldDefinition(TypeId(documentTypeCode), name, dataType, tenantId, isRequired, isUniqueKey);

    private static FieldDefinition CreateFieldDefinition(
        Guid documentTypeId, string name, FieldDataType dataType = FieldDataType.Text,
        Guid? tenantId = null, bool isRequired = false, bool isUniqueKey = false) =>
        new(
            id: FieldId(name),
            tenantId: tenantId,
            documentTypeId: documentTypeId,
            name: name,
            displayName: name,
            prompt: $"Extract the {name}.",
            dataType: dataType,
            displayOrder: 0,
            isRequired: isRequired,
            allowMultiple: false,
            isUniqueKey: isUniqueKey);

    private static Guid TypeId(string code) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + code)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}
