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
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class FieldExtractionServiceTestModule : AbpModule
{
    /// <summary>#491: a small ceiling keeps the oversized-body test from allocating a 200k-char string.</summary>
    public const int MarkdownCeiling = 64;

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        Configure<VaultExtractBehaviorOptions>(options =>
        {
            options.MaxFieldExtractionMarkdownLength = MarkdownCeiling;
        });

        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(),
            NullLogger<FieldExtractionWorkflow>.Instance);
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// Contract tests for the bulk entry point of <see cref="FieldExtractionService"/>
/// (<c>expectedEventTypeCode = null</c>) (#289 step 1).
/// Event-path behavior is guarded by <see cref="FieldExtractionEventHandler_Tests"/> through delegation; this
/// class focuses on bulk / single-document re-extraction semantics when calling the engine directly.
/// </summary>
public class FieldExtractionService_Tests
    : VaultExtractApplicationTestBase<FieldExtractionServiceTestModule>
{
    private readonly FieldExtractionService _service;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IDistributedEventBus _eventBus;

    public FieldExtractionService_Tests()
    {
        _service = GetRequiredService<FieldExtractionService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Batch_Path_Extracts_Against_Current_Type_And_Publishes()
    {
        var doc = CreateClassifiedDocument(typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateField("contract.general", "amount", FieldDataType.Number) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Extracted);
        result.FieldCount.ShouldBe(1);
        doc.ExtractedFieldValues.Single().FieldDefinitionId.ShouldBe(FieldId("amount"));
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.DocumentTypeCode == "contract.general" && e.FieldCount == 1),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Batch_Path_With_No_Definitions_Clears_And_Returns_Cleared()
    {
        var doc = CreateClassifiedDocument(typeCode: "blank.type");
        doc.SetFields(new[]
        {
            new DocumentFieldValue(FieldId("old"), FieldDataType.Number, JsonDocument.Parse("9").RootElement)
        });
        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Cleared);
        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Batch_Path_On_Unclassified_Document_Returns_Skipped()
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            new FileOrigin("blobs/x.pdf", "u", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1, "x.pdf"));
        // No DocumentTypeId (unclassified).
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Skipped);
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    /// <summary>
    /// #491: the core gate. A body over MaxFieldExtractionMarkdownLength must not reach the LLM at all — no call, no
    /// FieldsExtractedEto — and must leave a blocking review reason behind. The outcome is terminal (Declined, not an
    /// exception), because throwing would hand the job back to the ABP job store, which would re-send the same
    /// oversized body on every retry.
    /// </summary>
    [Fact]
    public async Task Batch_Path_Over_Ceiling_Declines_Without_Calling_The_Llm()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling + 1));
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateField("contract.general", "amount", FieldDataType.Number) });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Declined);
        await _workflow.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeTrue();
        ReviewReasonPolicy.HasBlocking(doc.ReviewReasons).ShouldBeTrue();
    }

    /// <summary>
    /// #491: declining must not destroy data. A host lowering the ceiling below an already-extracted document's length
    /// re-runs extraction (#289) and hits the gate; the values a previous in-budget run legitimately produced stay put.
    /// </summary>
    [Fact]
    public async Task Batch_Path_Over_Ceiling_Preserves_Previously_Extracted_Values()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling + 1));
        doc.SetFields(new[]
        {
            new DocumentFieldValue(FieldId("amount"), FieldDataType.Number, JsonDocument.Parse("1500").RootElement)
        });
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateField("contract.general", "amount", FieldDataType.Number) });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Declined);
        doc.ExtractedFieldValues.Single().FieldDefinitionId.ShouldBe(FieldId("amount"));
    }

    /// <summary>
    /// #491: a body exactly at the ceiling is in budget — the gate is <c>&gt;</c>, not <c>&gt;=</c>. Guards against an
    /// off-by-one that would decline the largest legitimately extractable document.
    /// </summary>
    [Fact]
    public async Task Batch_Path_Exactly_At_Ceiling_Still_Extracts()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling));
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateField("contract.general", "amount", FieldDataType.Number) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Extracted);
    }

    /// <summary>
    /// #491: the decline write carries the same stale-reclassify guard as the extraction write. If the document was
    /// reclassified between the phase-1 capture and the decline, the run is stale and must pin nothing — otherwise a
    /// stale run would leave a blocking reason on a document that has since moved to a type it may well fit.
    /// </summary>
    [Fact]
    public async Task Decline_On_A_Document_Reclassified_In_Flight_Pins_Nothing()
    {
        var doc = CreateClassifiedDocument(
            typeCode: "contract.general",
            markdown: new string('x', FieldExtractionServiceTestModule.MarkdownCeiling + 1));
        SetupType("contract.general");
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateField("contract.general", "amount", FieldDataType.Number) });

        // Phase 1 sees contract.general; by the time the decline reloads, an operator has reclassified the document.
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => doc, _ =>
            {
                Invoke(doc, "ApplyAutomaticClassificationResult", TypeId("invoice.general"), 0.99);
                return doc;
            });

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Skipped);
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    /// <summary>
    /// #491: once a run actually reaches the LLM — the host raised the ceiling — the blocking signal from the earlier
    /// declined run must be cleared, or the document would stay short of Ready forever.
    /// </summary>
    [Fact]
    public async Task Successful_Extraction_Clears_A_Previous_Decline()
    {
        var doc = CreateClassifiedDocument(typeCode: "contract.general");
        doc.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateField("contract.general", "amount", FieldDataType.Number) });
        _workflow.ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement });

        await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    /// <summary>
    /// #491: reclassifying a declined document to a type that declares no fields must clear the blocking signal — that
    /// type issues no LLM call, so nothing is "incomplete" and the operator has no action left to take.
    /// </summary>
    [Fact]
    public async Task Clearing_Path_For_A_Type_Without_Fields_Clears_A_Previous_Decline()
    {
        var doc = CreateClassifiedDocument(typeCode: "blank.type");
        doc.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        SetupType("blank.type");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        _fieldDefinitionRepository.GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var result = await _service.ExtractAsync(doc.Id, tenantId: null, expectedEventTypeCode: null);

        result.Outcome.ShouldBe(FieldExtractionOutcome.Cleared);
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private void SetupType(string code, Guid? typeId = null)
    {
        var id = typeId ?? TypeId(code);
        var type = new DocumentType(id, tenantId: null, typeCode: code, displayName: code);
        _documentTypeRepository.FindByTypeCodeAsync(code, Arg.Any<CancellationToken>()).Returns(type);
        _documentTypeRepository.FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
    }

    private static Document CreateClassifiedDocument(string typeCode, string markdown = "# Body")
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            new FileOrigin($"blobs/{Guid.NewGuid():N}.pdf", "u", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1, "x.pdf"));
        // Application.Tests has no InternalsVisibleTo; same as FieldExtractionEventHandler_Tests, use reflection
        // through the internal channel.
        Invoke(doc, "ApplyAutomaticClassificationResult", TypeId(typeCode), 0.99);
        Invoke(doc, "SetMarkdown", markdown);
        return doc;
    }

    private static void Invoke(Document doc, string method, params object[] args) =>
        typeof(Document)
            .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, args);

    private static FieldDefinition CreateField(string typeCode, string name, FieldDataType dataType) =>
        new(FieldId(name), tenantId: null, documentTypeId: TypeId(typeCode), name: name, displayName: name,
            prompt: $"Extract the {name}.", dataType: dataType);

    private static Guid TypeId(string code) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + code)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}
