using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Boolean;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.FlexFields.Tags;
using Dignite.Vault.Extract.Documents.Pipelines;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.EventBus.Distributed;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Behavior tests for <see cref="DocumentAppService.UpdateExtractedFieldsAsync"/> (manual metadata edits
/// #195). Reuses mock dependencies from <see cref="DocumentAppServiceReviewTestModule"/> plus real DI
/// components such as ObjectMapper.
/// </summary>
public class DocumentAppService_ExtractedFields_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentAppService_ExtractedFields_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    /// <summary>
    /// #491: manual entry is the only escape from the blocking <c>FieldExtractionIncomplete</c> reason — no operator
    /// action can shrink a document's Markdown. Writing the values by hand means the human did the work the LLM
    /// declined, so the reason clears and the Ready gate is re-derived. Without this the reason would be a dead end.
    /// </summary>
    [Fact]
    public async Task Manual_Entry_Clears_The_Blocking_FieldExtractionIncomplete_Reason()
    {
        var doc = CreateClassifiedDocument("host.contract");
        doc.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        ReviewReasonPolicy.HasBlocking(doc.ReviewReasons).ShouldBeTrue();
        StubGet(doc);
        StubFields("host.contract", "amount");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement> { ["amount"] = JsonString("1000") }
        });

        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
        ReviewReasonPolicy.HasBlocking(doc.ReviewReasons).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Write_Fields()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount", "party");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonString("1000"),
                ["party"] = JsonString("Acme")
            }
        });

        doc.FlexFields.Count.ShouldBe(2);
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ─── #650: DocumentReadyEto re-announce from UpdateExtractedFieldsAsync ───

    /// <summary>
    /// #650: an operator editing fields on an already-Ready document changes consumable content with no
    /// lifecycle transition to announce it, so UpdateExtractedFieldsAsync re-publishes DocumentReadyEto itself.
    /// The document is legitimately driven to Ready first, through the real DocumentPipelineRunManager +
    /// its in-memory fake run repository (not a LifecycleStatus shortcut), so <c>wasReady</c> observes a real
    /// pre-edit Ready state.
    /// </summary>
    [Fact]
    public async Task Already_Ready_Document_Edit_Republishes_DocumentReadyEto()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount", "party");
        await SucceedAllKeyPipelinesAsync(doc);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonString("1000"),
                ["party"] = JsonString("Acme")
            }
        });

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentReadyEto>(e => e.DocumentId == doc.Id),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    /// <summary>
    /// #650: the #491 path. The document already has every key pipeline Succeeded but carries the blocking
    /// FieldExtractionIncomplete reason, so it sits in PendingReview, not Ready, before the edit. Manual entry
    /// clears the reason and this same edit derives the document into Ready for the first time. That transition
    /// is announced once, by DocumentReadyEventHandler reacting to the lifecycle-changed local event (see
    /// DocumentReadyEventHandler_Tests) — not from here. The <c>wasReady</c> guard must stop
    /// UpdateExtractedFieldsAsync from ALSO publishing, or the same transition would double-fire.
    /// </summary>
    [Fact]
    public async Task Transition_Into_Ready_By_This_Edit_Does_Not_Double_Publish_DocumentReadyEto()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount");
        await SucceedAllKeyPipelinesAsync(doc);
        doc.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        await _pipelineRunManager.ReDeriveLifecycleAsync(doc);
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.PendingReview);

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement> { ["amount"] = JsonString("1000") }
        });

        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Ready);
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentReadyEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    /// <summary>#650: a document that never reached Ready (a key pipeline never ran) stays not-Ready after the
    /// edit, so nothing is republished.</summary>
    [Fact]
    public async Task Not_Ready_Document_Edit_Does_Not_Publish_DocumentReadyEto()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount");
        // No pipeline runs recorded for this document, so it was never Ready and this edit cannot make it so.

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement> { ["amount"] = JsonString("1000") }
        });

        doc.LifecycleStatus.ShouldNotBe(DocumentLifecycleStatus.Ready);
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentReadyEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Should_Reject_Unknown_Field_Key()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement> { ["unknown"] = JsonString("x") }
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);
    }

    [Fact]
    public async Task Should_Accept_Values_That_Match_Field_DataTypes()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields(
            "host.contract",
            ("title", TextFieldType.ControlName, TextConfig()),
            ("count", NumberFieldType.ControlName, Empty()),
            ("amount", NumberFieldType.ControlName, Empty()),
            ("approved", BooleanFieldType.ControlName, Empty()),
            ("date", DateTimeFieldType.ControlName, DateConfig(DateTimeInputMode.Date)),
            ("occurredAt", DateTimeFieldType.ControlName, DateConfig(DateTimeInputMode.DateTime)));

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>
            {
                ["title"] = JsonValue("Acme"),
                ["count"] = JsonValue(7),
                ["amount"] = JsonValue(123.45m),
                ["approved"] = JsonValue(true),
                ["date"] = JsonValue("2026-05-22"),
                ["occurredAt"] = JsonValue("2026-05-22T18:30:00")
            }
        });

        doc.FlexFields.Count.ShouldBe(6);
    }

    [Fact]
    public async Task Should_Reject_Value_When_DataType_Does_Not_Match()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", ("amount", NumberFieldType.ControlName, Empty()));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>
                {
                    ["amount"] = JsonValue("123.45")
                }
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("amount");
    }

    [Fact]
    public async Task Should_Reject_DateTime_With_Timezone_Offset()
    {
        // DateTime fields accept only offset-free wall-clock values. Offset / Z values conflict with
        // query-side datetime2 semantics, so the operator manual-edit path must reject them too. This
        // shares ExtractedFieldValueValidator with the LLM extraction path (Codex review finding 2).
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", ("occurredAt", DateTimeFieldType.ControlName, DateConfig(DateTimeInputMode.DateTime)));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>
                {
                    ["occurredAt"] = JsonValue("2026-05-22T18:30:00+08:00")
                }
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("occurredAt");
    }

    [Fact]
    public async Task Should_Clear_All_Fields_When_Input_Is_Empty()
    {
        var doc = CreateClassifiedDocument("host.contract");
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = "1000" });
        doc.FlexFields.Count.ShouldBe(1);
        StubGet(doc);
        StubFields("host.contract", "amount");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>()
        });

        // Empty input clears all field rows as a group.
        doc.FlexFields.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_Expand_MultiValue_String_Field_Into_Ordered_Rows()
    {
        // #212: multi-value text field. JSON array input is split by the App layer through
        // DocumentFieldValueFactory into multiple rows with Order 0,1,2...
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubMultiField("host.contract", "tags");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>
            {
                ["tags"] = JsonValue(new[] { "urgent", "legal", "2026" })
            }
        });

        // v3: the whole array is one bag entry keyed by the field name, in submitted order — the
        // per-item Order column v2 needed is gone with the value rows it ordered.
        doc.FlexFields.Count.ShouldBe(1);
        doc.FlexFields["tags"].ShouldBeOfType<List<string>>()
            .ShouldBe(new[] { "urgent", "legal", "2026" });
    }

    [Fact]
    public async Task Should_Render_MultiValue_Field_As_Array_In_Returned_Dto()
    {
        // #212: read/write symmetry. Multi-value fields are written as arrays and output DTOs render JSON
        // arrays too, keeping operator read-edit-save round trips consistent.
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        var tags = MultiField(TypeId("host.contract"), "tags");
        // Write-path resolution by looking up definitions by typeId.
        _fieldRepository.GetListAsync(TypeId("host.contract"), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { tags });
        // Read-path resolution: MapToDtoAsync -> ResolveReferenceMapsAsync queries definitions by
        // predicate for Name/DataType/AllowMultiple.
        _fieldRepository.GetListAsync(
            Arg.Any<Expression<Func<Field, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { tags });

        var dto = await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>
            {
                ["tags"] = JsonValue(new[] { "urgent", "legal", "2026" })
            }
        });

        dto.ExtractedFields.ShouldNotBeNull();
        var tagsValue = dto.ExtractedFields!["tags"];
        tagsValue.ValueKind.ShouldBe(JsonValueKind.Array);
        tagsValue.EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "urgent", "legal", "2026" });
    }

    [Fact]
    public async Task Should_Reject_Scalar_For_MultiValue_Field()
    {
        // Scalar input for a multi-value field, meaning non-array, is type-incompatible and fails loudly,
        // same as a single-value field type mismatch.
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubMultiField("host.contract", "tags");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement> { ["tags"] = JsonString("urgent") }
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("tags");
    }

    [Fact]
    public async Task Should_Reject_When_Document_Not_Classified()
    {
        var doc = CreateDocument(); // DocumentTypeCode is null
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>()
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.NotClassified);
    }

    // ─── §9: explicit operator resolution of field validation warnings ───

    [Fact]
    public async Task Resolve_Clears_Selected_Warnings_And_Their_Blocking_Bit()
    {
        var amountFieldId = Guid.NewGuid();
        var doc = CreateClassifiedDocument("host.contract");
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(amountFieldId, "does not reconcile") });
        ReviewReasonPolicy.HasBlocking(doc.ReviewReasons).ShouldBeTrue();
        StubFindWithFieldValues(doc);

        await _appService.ResolveFieldValidationWarningsAsync(doc.Id, new ResolveFieldValidationWarningsInput
        {
            FieldDefinitionIds = new List<Guid> { amountFieldId }
        });

        doc.FieldValidationWarnings.ShouldBeEmpty();
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldValidationWarning).ShouldBeFalse();
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_Is_Rejected_While_Field_Extraction_Is_In_Progress()
    {
        // #527 §9: a pending/running field-extraction run would replace the whole warning set on completion and
        // overwrite the human decision, so resolution is rejected while it is in flight.
        var amountFieldId = Guid.NewGuid();
        var doc = CreateClassifiedDocument("host.contract");
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(amountFieldId, "does not reconcile") });
        StubFindWithFieldValues(doc);
        // Seed a pending field-extraction run for this document (drives EnsureNotInProgressAsync).
        await _pipelineRunManager.QueueAsync(doc, VaultExtractPipelines.FieldExtraction);

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.ResolveFieldValidationWarningsAsync(doc.Id, new ResolveFieldValidationWarningsInput
            {
                FieldDefinitionIds = new List<Guid> { amountFieldId }
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Pipeline.RetryInProgress);
        doc.FieldValidationWarnings.Count.ShouldBe(1);   // untouched — rejected before resolving
    }

    [Fact]
    public async Task Manual_Field_Edit_Does_Not_Clear_Validation_Warnings()
    {
        // #527 §9: saving a manual correction leaves the warning visible until the operator explicitly resolves it —
        // UpdateExtractedFieldsAsync must not clear the warning or its blocking bit.
        var amountFieldId = Guid.NewGuid();
        var doc = CreateClassifiedDocument("host.contract");
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(amountFieldId, "does not reconcile") });
        StubGet(doc);
        StubFields("host.contract", "amount");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement> { ["amount"] = JsonString("1000") }
        });

        doc.FieldValidationWarnings.Count.ShouldBe(1);
        doc.ReviewReasons.HasFlag(DocumentReviewReasons.FieldValidationWarning).ShouldBeTrue();
    }

    [Fact]
    public async Task Detail_Dto_Exposes_Field_Validation_Warnings_As_A_Blocking_Review_Reason()
    {
        // #527 §10: the REST detail DTO projects the warning with the field's current name / display name + message,
        // marked blocking. The detail read loads warnings via the field-stage loader.
        var amountFieldId = Guid.NewGuid();
        var doc = CreateClassifiedDocument("host.contract");
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(amountFieldId, "does not reconcile") });
        StubFindWithFieldValues(doc);
        // Warned-field name/display-name resolution (soft-delete-disabled predicate lookup).
        _fieldRepository
            .GetListAsync(Arg.Any<Expression<Func<Field, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Field>
            {
                new(amountFieldId, tenantId: null, documentTypeId: TypeId("host.contract"),
                    name: "amount", displayName: "Amount",
                    fieldTypeName: NumberFieldType.ControlName, description: "extract amount")
            });

        var dto = await _appService.GetAsync(doc.Id);

        var detail = dto.ReviewReasonDetails!.Single(d => d.Reason == DocumentReviewReasons.FieldValidationWarning);
        detail.IsBlocking.ShouldBeTrue();
        detail.FieldValidationWarnings!.Count.ShouldBe(1);
        detail.FieldValidationWarnings[0].FieldDefinitionId.ShouldBe(amountFieldId);
        detail.FieldValidationWarnings[0].FieldName.ShouldBe("amount");
        detail.FieldValidationWarnings[0].FieldDisplayName.ShouldBe("Amount");
        detail.FieldValidationWarnings[0].Message.ShouldBe("does not reconcile");
    }

    private void StubGet(Document doc)
    {
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // #527: the DocumentDto write paths (UpdateExtractedFields / Reject / AllowDuplicate / UpdateCabinet) load via
        // FindWithFieldValuesAsync so the returned DTO carries warning details; stub it to the same instance so those
        // tests resolve the document rather than hitting the not-found guard.
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void StubFindWithFieldValues(Document doc)
    {
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    /// <summary>
    /// #650: drives every key pipeline (Parse / Classification / FieldExtraction) to Succeeded through the real
    /// <see cref="DocumentPipelineRunManager"/> + its in-memory fake run repository, so
    /// <c>ReDeriveLifecycleAsync</c> can legitimately derive <see cref="DocumentLifecycleStatus.Ready"/> — the
    /// same mechanism production code goes through, not a <c>LifecycleStatus</c> shortcut.
    /// </summary>
    private async Task SucceedAllKeyPipelinesAsync(Document doc)
    {
        foreach (var pipelineCode in VaultExtractPipelines.KeyPipelines)
        {
            var run = await _pipelineRunManager.StartAsync(doc, pipelineCode);
            await _pipelineRunManager.CompleteAsync(doc, run);
        }
    }

    private void StubFields(string typeCode, params string[] names)
    {
        StubFields(
            typeCode,
            names.Select(n => (Name: n, TypeName: TextFieldType.ControlName, Config: TextConfig())).ToArray());
    }

    private void StubFields(
        string typeCode,
        params (string Name, string TypeName, FieldConfigurationDictionary Config)[] fields)
    {
        var defs = fields
            .Select(f => new Field(
                Guid.NewGuid(), tenantId: null, documentTypeId: TypeId(typeCode),
                name: f.Name, displayName: f.Name,
                fieldTypeName: f.TypeName, description: "extract " + f.Name, configuration: f.Config))
            .ToList();
        _fieldRepository.GetListAsync(TypeId(typeCode), Arg.Any<CancellationToken>())
            .Returns(defs);
    }

    // #212, now #559 resolution 6: multi-value is a property of the field type rather than a flag beside
    // it, so v2's AllowMultiple text field is Tags — the open-vocabulary type, as the migrator maps it.
    private void StubMultiField(string typeCode, string name)
    {
        _fieldRepository.GetListAsync(TypeId(typeCode), Arg.Any<CancellationToken>())
            .Returns(new List<Field> { MultiField(TypeId(typeCode), name) });
    }

    private static Field MultiField(Guid typeId, string name)
        => new(
            Guid.NewGuid(), tenantId: null, documentTypeId: typeId,
            name: name, displayName: name,
            fieldTypeName: TagsFieldType.ControlName, description: "extract " + name,
            configuration: new TagsConfiguration().ConfigurationDictionary);

    private static FieldConfigurationDictionary Empty() => new();

    private static FieldConfigurationDictionary TextConfig()
        => new TextConfiguration { Mode = TextMode.SingleLine }.ConfigurationDictionary;

    private static FieldConfigurationDictionary DateConfig(DateTimeInputMode mode)
        => new DateTimeConfiguration { InputMode = mode }.ConfigurationDictionary;

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    private static Document CreateClassifiedDocument(string typeCode)
    {
        var doc = CreateDocument();
        // DocumentTypeId is set through a Domain-internal method; the test project has internal access
        // only to Application, so it cannot call Document.ConfirmClassification. Use reflection to set the
        // private setter and simulate "classified" (#207 internally associates by id).
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId(typeCode));
        return doc;
    }

    // typeCode to stable Guid derivation (#207: internal association by DocumentTypeId), matching StubFields.
    private static Guid TypeId(string typeCode)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("type:" + typeCode)));

    private static JsonElement JsonString(string value)
    {
        return JsonValue(value);
    }

    private static JsonElement JsonValue<T>(T value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }
}
