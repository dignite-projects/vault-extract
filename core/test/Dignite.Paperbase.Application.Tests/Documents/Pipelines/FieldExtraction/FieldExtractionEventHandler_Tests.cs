using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Documents.Fields;
using Dignite.Paperbase.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class FieldExtractionEventHandlerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // FieldExtractionWorkflow 是具体类——用 ForPartsOf + 假 ctor 依赖，
        // 测试 case 内对 virtual ExtractAsync 设 Returns/Throws。
        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new PaperbaseAIBehaviorOptions()),
            NullLogger<FieldExtractionWorkflow>.Instance);
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// <see cref="FieldExtractionEventHandler"/> 行为测试——重点覆盖三类正确性约束：
/// <list type="number">
///   <item>**Reclassify race**：飞行期间被操作员 reclassify 的 stale 事件必须丢弃（不能用旧 schema 写入污染 ExtractedFields）</item>
///   <item>**Cross-tenant 防护**：事件 TenantId 与 Document.TenantId 不一致时丢弃（防 DataFilter disable 路径泄漏）</item>
///   <item>**ETO 契约**：FieldsExtractedEto 在空字段 / 有字段两条路径下都按 outbox 语义发布</item>
/// </list>
/// #207：handler 以 Document 当前 DocumentTypeId 为准读字段定义；事件 TypeCode 只作为 stale reclassify 辅助判断。
/// 测试用 name/code → 稳定 Guid 派生保证 mock 一致。
/// </summary>
public class FieldExtractionEventHandler_Tests
    : PaperbaseApplicationTestBase<FieldExtractionEventHandlerTestModule>
{
    private readonly FieldExtractionEventHandler _handler;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IDistributedEventBus _eventBus;

    public FieldExtractionEventHandler_Tests()
    {
        _handler = GetRequiredService<FieldExtractionEventHandler>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Empty_DocumentTypeCode_Returns_Early_Without_Publishing()
    {
        var evt = new DocumentClassifiedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = string.Empty,
            ClassificationConfidence = 0
        };

        await _handler.HandleEventAsync(evt);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _fieldDefinitionRepository.DidNotReceive().GetForExtractionAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Field_Definitions_Publishes_Empty_FieldsExtractedEto()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        // 即使没有字段定义，也要发空事件让下游 DocumentReady 推进
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());

        // LLM 不应被调用——无字段定义直接 short-circuit
        await _workflow.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Field_Definitions_Clears_Stale_Fields_From_Previous_Type()
    {
        // reclassify 到无字段定义的类型：旧 schema 残留字段行必须被清空（#206 验收「reclassify 不残留旧 schema」+ 审查发现 #1）。
        var doc = CreateDocument(tenantId: null, typeCode: "blank.type");
        doc.SetFields(new[]
        {
            new DocumentFieldValue(FieldId("amount"), FieldDataType.Decimal, JsonDocument.Parse("100").RootElement)
        });
        doc.ExtractedFieldValues.ShouldNotBeEmpty();

        SetupType("blank.type");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "blank.type",
            ClassificationConfidence = 1.0
        };

        await _handler.HandleEventAsync(evt);

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
        var defs = new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount") };
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement });
        _documentRepository
            .FindAsync(docId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        var evt = new DocumentClassifiedEto
        {
            DocumentId = docId,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        // FieldsExtractedEto 不应发布——Document 都没了，下游消费方没用
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Cross_Tenant_Event_Is_Discarded_Without_Writing_Fields()
    {
        // CLAUDE.md "## 安全约定 / 多租户隔离"：
        // 事件 TenantId 与 Document.TenantId 不一致 → 防 DataFilter disable 路径泄漏
        var eventTenant = Guid.NewGuid();
        var docTenant = Guid.NewGuid();   // 不同租户
        var doc = CreateDocument(tenantId: docTenant, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount", tenantId: eventTenant) });
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = eventTenant,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Stale_TypeCode_From_Reclassify_Race_Is_Discarded()
    {
        // Reclassify race 防护——核心安全约束：
        // 事件载荷 DocumentTypeCode=contract.general，但 Document 当前已被操作员
        // reclassify 成 invoice.general（DocumentTypeId 不同）。继续抽取会用旧 schema (contract) 写入
        // ExtractedFields，造成"TypeId=invoice 但字段来自 contract"的脏状态。
        // 正确做法：丢弃 stale 事件，等新分类事件触发新一轮抽取。
        var doc = CreateDocument(tenantId: null, typeCode: "invoice.general");
        SetupType("contract.general");
        SetupType("invoice.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount") });
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement });

        var staleEvent = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",   // ← stale typeCode（resolves to a different typeId than doc）
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(staleEvent);

        // 关键断言：不能把基于 contract schema 抽取的字段写入 Document（现在 typeId=invoice）
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
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        // 类型与 DataType 对齐（生产中 workflow 已校验类型；amount=Decimal 数字、party=String 字符串、date=Date）。
        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Decimal),
            CreateFieldDefinition("contract.general", "party", FieldDataType.String),
            CreateFieldDefinition("contract.general", "date", FieldDataType.Date)
        };
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement,
                ["party"] = JsonDocument.Parse("\"Acme Corp\"").RootElement,
                ["date"] = null   // LLM 未能抽到——不应进入字段集
            });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        // 字段值按 FieldDefinitionId 写入（#207）；null 值不入字段集。
        var fieldIds = doc.ExtractedFieldValues.Select(f => f.FieldDefinitionId).ToList();
        fieldIds.Count.ShouldBe(2);
        fieldIds.ShouldContain(FieldId("amount"));
        fieldIds.ShouldContain(FieldId("party"));
        fieldIds.ShouldNotContain(FieldId("date"));

        await _documentRepository.Received(1).UpdateAsync(
            doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // FieldCount 等于实际非空字段数，不是 definition 总数
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.FieldCount == 2),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Renamed_TypeCode_Event_Uses_Current_DocumentTypeId_And_Publishes_Current_Code()
    {
        // TypeCode rename race：事件里还是旧 code，但 DocumentTypeId 是稳定内部关联。
        // 旧 code 解析不到时不应跳过抽取；应按当前类型 Id 抽取并发布当前 TypeCode。
        var typeId = TypeId("contract.general");
        var doc = CreateDocument(tenantId: null, documentTypeId: typeId);
        SetupType("contract.renamed", typeId: typeId);
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition(typeId, "amount", FieldDataType.Decimal)
        };
        _fieldDefinitionRepository
            .GetForExtractionAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement
            });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",   // old code, now unresolvable
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.Count.ShouldBe(1);
        doc.ExtractedFieldValues.Single().FieldDefinitionId.ShouldBe(FieldId("amount"));

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.renamed" &&
                e.FieldCount == 1),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task DataType_Changed_During_Extraction_Skips_Stale_Value()
    {
        // LLM 调用期间 admin 把字段类型 Decimal 改成 String：旧 descriptor 抽到的 number 不能写进当前 String 字段。
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        var initialDefs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Decimal)
        };
        var currentDefs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.String)
        };
        _fieldDefinitionRepository
            .GetForExtractionAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(initialDefs, currentDefs);
        _workflow
            .ExtractAsync(
                Arg.Is<IReadOnlyList<FieldExtractionDescriptor>>(d =>
                    d.Count == 1 && d[0].Name == "amount" && d[0].DataType == FieldDataType.Decimal),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement
            });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.Received(1).UpdateAsync(
            doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private void SetupType(string code, Guid? tenantId = null, Guid? typeId = null)
    {
        var id = typeId ?? TypeId(code);
        var type = new DocumentType(id, tenantId, code, code);
        _documentTypeRepository
            .FindByTypeCodeAsync(code, Arg.Any<CancellationToken>())
            .Returns(type);
        _documentTypeRepository
            .FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(type);
    }

    private static Document CreateDocument(Guid? tenantId, string typeCode)
        => CreateDocument(tenantId, TypeId(typeCode));

    private static Document CreateDocument(Guid? tenantId, Guid documentTypeId)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        // 走 internal 通道写入 DocumentTypeId 模拟分类已完成状态（#207：分类结果是内部 Id）
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [documentTypeId, 0.99]);
        typeof(Document)
            .GetMethod("SetMarkdown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, ["# Body"]);

        return doc;
    }

    private static FieldDefinition CreateFieldDefinition(
        string documentTypeCode, string name,
        FieldDataType dataType = FieldDataType.String, Guid? tenantId = null) =>
        CreateFieldDefinition(TypeId(documentTypeCode), name, dataType, tenantId);

    private static FieldDefinition CreateFieldDefinition(
        Guid documentTypeId, string name,
        FieldDataType dataType = FieldDataType.String, Guid? tenantId = null) =>
        new(
            id: FieldId(name),
            tenantId: tenantId,
            documentTypeId: documentTypeId,
            name: name,
            displayName: name,
            prompt: $"Extract the {name}.",
            dataType: dataType);

    private static Guid TypeId(string code) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + code)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}
