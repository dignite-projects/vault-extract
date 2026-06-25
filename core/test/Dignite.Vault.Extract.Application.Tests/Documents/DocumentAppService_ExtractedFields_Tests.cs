using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
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
    : ExtractApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDistributedEventBus _eventBus;

    public DocumentAppService_ExtractedFields_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Should_Write_Fields_And_Republish_FieldsExtractedEto()
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

        doc.ExtractedFieldValues.Count.ShouldBe(2);
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received().PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 2),
            Arg.Any<bool>(),
            Arg.Any<bool>());
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

        ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.Unknown);
    }

    [Fact]
    public async Task Should_Accept_Values_That_Match_Field_DataTypes()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields(
            "host.contract",
            ("title", FieldDataType.Text),
            ("count", FieldDataType.Number),
            ("amount", FieldDataType.Number),
            ("approved", FieldDataType.Boolean),
            ("date", FieldDataType.Date),
            ("occurredAt", FieldDataType.DateTime));

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

        doc.ExtractedFieldValues.Count.ShouldBe(6);
    }

    [Fact]
    public async Task Should_Reject_Value_When_DataType_Does_Not_Match()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", ("amount", FieldDataType.Number));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>
                {
                    ["amount"] = JsonValue("123.45")
                }
            }));

        ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.InvalidValue);
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
        StubFields("host.contract", ("occurredAt", FieldDataType.DateTime));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>
                {
                    ["occurredAt"] = JsonValue("2026-05-22T18:30:00+08:00")
                }
            }));

        ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("occurredAt");
    }

    [Fact]
    public async Task Should_Clear_All_Fields_When_Input_Is_Empty()
    {
        var doc = CreateClassifiedDocument("host.contract");
        doc.SetFields(new[] { new DocumentFieldValue(Guid.NewGuid(), FieldDataType.Text, JsonString("1000")) });
        doc.ExtractedFieldValues.Count.ShouldBe(1);
        StubGet(doc);
        StubFields("host.contract", "amount");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>()
        });

        // Empty input clears all field rows as a group; FieldsExtractedEto is republished with
        // FieldCount = 0.
        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _eventBus.Received().PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 0),
            Arg.Any<bool>(),
            Arg.Any<bool>());
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

        // Array becomes 3 rows and is restored by Order.
        doc.ExtractedFieldValues.Count.ShouldBe(3);
        doc.ExtractedFieldValues.OrderBy(f => f.Order).Select(f => f.TextValue)
            .ShouldBe(new[] { "urgent", "legal", "2026" });

        // FieldsExtractedEto.FieldCount is logical field count (1), not expanded row count (3).
        await _eventBus.Received().PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 1),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task Should_Render_MultiValue_Field_As_Array_In_Returned_Dto()
    {
        // #212: read/write symmetry. Multi-value fields are written as arrays and output DTOs render JSON
        // arrays too, keeping operator read-edit-save round trips consistent.
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        var tags = new FieldDefinition(
            Guid.NewGuid(), tenantId: null, documentTypeId: TypeId("host.contract"),
            name: "tags", displayName: "Tags", prompt: "extract tags",
            dataType: FieldDataType.Text, allowMultiple: true);
        // Write-path resolution by looking up definitions by typeId.
        _fieldDefinitionRepository.GetListAsync(TypeId("host.contract"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { tags });
        // Read-path resolution: MapToDtoAsync -> ResolveReferenceMapsAsync queries definitions by
        // predicate for Name/DataType/AllowMultiple.
        _fieldDefinitionRepository.GetListAsync(
            Arg.Any<Expression<Func<FieldDefinition, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { tags });

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

        ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.InvalidValue);
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

        ex.Code.ShouldBe(ExtractErrorCodes.Document.NotClassified);
    }

    private void StubGet(Document doc)
    {
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void StubFields(string typeCode, params string[] names)
    {
        StubFields(
            typeCode,
            names.Select(n => (Name: n, DataType: FieldDataType.Text)).ToArray());
    }

    private void StubFields(string typeCode, params (string Name, FieldDataType DataType)[] fields)
    {
        var defs = fields
            .Select(f => new FieldDefinition(
                Guid.NewGuid(), tenantId: null, documentTypeId: TypeId(typeCode),
                name: f.Name, displayName: f.Name, prompt: "extract " + f.Name, dataType: f.DataType))
            .ToList();
        _fieldDefinitionRepository.GetListAsync(TypeId(typeCode), Arg.Any<CancellationToken>())
            .Returns(defs);
    }

    // #212: stub a multi-value text field definition (AllowMultiple = true).
    private void StubMultiField(string typeCode, string name)
    {
        var defs = new List<FieldDefinition>
        {
            new(Guid.NewGuid(), tenantId: null, documentTypeId: TypeId(typeCode),
                name: name, displayName: name, prompt: "extract " + name,
                dataType: FieldDataType.Text, allowMultiple: true)
        };
        _fieldDefinitionRepository.GetListAsync(TypeId(typeCode), Arg.Any<CancellationToken>())
            .Returns(defs);
    }

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
