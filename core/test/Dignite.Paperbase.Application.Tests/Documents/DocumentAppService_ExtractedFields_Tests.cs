using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.EventBus.Distributed;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// <see cref="DocumentAppService.UpdateExtractedFieldsAsync"/> 行为测试（元数据手改 #195）。
/// 复用 <see cref="DocumentAppServiceReviewTestModule"/> 的 mock 依赖 + 真实 DI（ObjectMapper 等）。
/// </summary>
public class DocumentAppService_ExtractedFields_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceReviewTestModule>
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

        ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.Unknown);
    }

    [Fact]
    public async Task Should_Accept_Values_That_Match_Field_DataTypes()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields(
            "host.contract",
            ("title", FieldDataType.String),
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

        ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("amount");
    }

    [Fact]
    public async Task Should_Reject_DateTime_With_Timezone_Offset()
    {
        // DateTime 字段只接受无偏移 wall-clock——带偏移 / Z 的值与查询侧 datetime2 语义不一致，
        // 操作员手改路径也应拒绝（与 LLM 抽取路径共用 ExtractedFieldValueValidator；Codex 评审 finding 2）。
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

        ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("occurredAt");
    }

    [Fact]
    public async Task Should_Clear_All_Fields_When_Input_Is_Empty()
    {
        var doc = CreateClassifiedDocument("host.contract");
        doc.SetFields(new[] { new DocumentFieldValue(Guid.NewGuid(), FieldDataType.String, JsonString("1000")) });
        doc.ExtractedFieldValues.Count.ShouldBe(1);
        StubGet(doc);
        StubFields("host.contract", "amount");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>()
        });

        // 传空 → 整组清空全部字段行；复用 FieldsExtractedEto 重发，FieldCount = 0。
        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _eventBus.Received().PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 0),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task Should_Expand_MultiValue_String_Field_Into_Ordered_Rows()
    {
        // #212：多值 String 字段——输入 JSON 数组，App 层经 DocumentFieldValueFactory 拆成多行（Order 0,1,2…）。
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

        // 数组 → 3 行，按 Order 还原。
        doc.ExtractedFieldValues.Count.ShouldBe(3);
        doc.ExtractedFieldValues.OrderBy(f => f.Order).Select(f => f.StringValue)
            .ShouldBe(new[] { "urgent", "legal", "2026" });

        // FieldsExtractedEto.FieldCount 是逻辑字段数（1），非展开行数（3）。
        await _eventBus.Received().PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 1),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task Should_Render_MultiValue_Field_As_Array_In_Returned_Dto()
    {
        // #212：读写对称——多值字段写入数组，出口 DTO 也渲染为 JSON 数组（让 operator 读—改—存往返一致）。
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        var tags = new FieldDefinition(
            Guid.NewGuid(), tenantId: null, documentTypeId: TypeId("host.contract"),
            name: "tags", displayName: "Tags", prompt: "extract tags",
            dataType: FieldDataType.String, allowMultiple: true);
        // 写路径解析（按 typeId 查定义）
        _fieldDefinitionRepository.GetListAsync(TypeId("host.contract"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { tags });
        // 读路径解析（MapToDtoAsync → ResolveReferenceMapsAsync 按 predicate 查定义拿 Name/DataType/AllowMultiple）
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
        // 多值字段收到标量（非数组）→ 不合类型 → loud fail（与单值字段类型不符同路径）。
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubMultiField("host.contract", "tags");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement> { ["tags"] = JsonString("urgent") }
            }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.InvalidValue);
        ex.Data["FieldName"].ShouldBe("tags");
    }

    [Fact]
    public async Task Should_Reject_When_Document_Not_Classified()
    {
        var doc = CreateDocument(); // DocumentTypeCode 为 null
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>()
            }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.Document.NotClassified);
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
            names.Select(n => (Name: n, DataType: FieldDataType.String)).ToArray());
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

    // #212：stub 一个多值 String 字段定义（AllowMultiple = true）。
    private void StubMultiField(string typeCode, string name)
    {
        var defs = new List<FieldDefinition>
        {
            new(Guid.NewGuid(), tenantId: null, documentTypeId: TypeId(typeCode),
                name: name, displayName: name, prompt: "extract " + name,
                dataType: FieldDataType.String, allowMultiple: true)
        };
        _fieldDefinitionRepository.GetListAsync(TypeId(typeCode), Arg.Any<CancellationToken>())
            .Returns(defs);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            originalFileBlobName: $"blobs/{Guid.NewGuid():N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    private static Document CreateClassifiedDocument(string typeCode)
    {
        var doc = CreateDocument();
        // DocumentTypeId 经 Domain internal 方法设置；测试项目只对 Application 开放 internal，
        // 不能调 Document.ConfirmClassification —— 用反射写 private setter 模拟"已分类"（#207 内部按 Id 关联）。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId(typeCode));
        return doc;
    }

    // typeCode → 稳定 Guid 派生（#207：内部按 DocumentTypeId 关联；与 StubFields 一致）。
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
