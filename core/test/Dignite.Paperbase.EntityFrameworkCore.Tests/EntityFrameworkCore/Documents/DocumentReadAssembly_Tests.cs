using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// 出口 DTO 的 <c>ExtractedFields</c> wire-format 由 App / Mapper 层从 <see cref="DocumentExtractedField"/> typed child 行
/// 即时组装（Issue #206 + #207）。本测试走真实 EF（SQLite）验证读路径的两个机制：
/// <list type="bullet">
///   <item><c>WithDetailsAsync(选择器)</c>——<c>GetListAsync</c> 用来 eager-load child 行的 ABP 仓储 API：一次 JOIN
///   取回，不依赖 lazy loading（lazy 在测试 / 生产都未启用，此测试通过即证明组装不触发 N+1 / lazy）；</item>
///   <item><see cref="DocumentExtractedField.ToJsonElement"/>——把各 DataType 的类型化列重建为规范 JSON，与写入侧
///   <c>SetValue</c> 往返一致。</item>
/// </list>
/// #207：child 行内部按 <see cref="DocumentExtractedField.FieldDefinitionId"/> 索引（不再存字段名），出口字典 key
/// （字段名）由 App 层 join 当前 <c>FieldDefinition</c> 解析——name 解析的接线由 Application.Tests 覆盖；本测试只验证
/// typed-column 往返，故按 FieldDefinitionId 键值断言。
/// </summary>
public class DocumentReadAssembly_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private const string TypeCode = "host.invoice";

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentReadAssembly_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task WithDetails_eager_loads_child_rows_and_round_trips_every_FieldDataType()
    {
        // 穷尽性绊线（#208）：遍历所有 FieldDataType。加新枚举值若不在 SampleFor 补样本，建样本时即抛错（红）；
        // 补样本后往返会跑过 SetValue + ToJsonElement，那两处漏处理新类型也会抛（红）——守住实体的两处 typed-column switch。
        var dataTypes = Enum.GetValues<FieldDataType>();
        var samples = dataTypes.ToDictionary(dt => dt, SampleFor);

        var id = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => InsertAsync(id,
            dataTypes.Select(dt => new DocumentFieldValue(FieldIdFor(dt), dt, samples[dt].Value)).ToArray()));

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);
            var doc = (await query.Where(d => d.Id == id).ToListAsync()).Single();

            // #208：字段类型由 FieldDefinition 决定（不在字段值行持久化）；读路径 join 取 DataType 后重建 JSON。
            var types = (await _fieldDefinitionRepository.GetListAsync()).ToDictionary(f => f.Id, f => f.DataType);

            // typed-column → 规范 JSON 往返（key 用 FieldDefinitionId；字段名解析是 App 层 join，不在此层断言）。
            var fields = doc.ExtractedFieldValues.ToDictionary(
                f => f.FieldDefinitionId, f => f.ToJsonElement(types[f.FieldDefinitionId]));

            fields.Count.ShouldBe(dataTypes.Length);
            foreach (var dt in dataTypes)
            {
                samples[dt].AssertRoundTrip(fields[FieldIdFor(dt)]);
            }
        });
    }

    [Fact]
    public async Task Document_without_fields_has_empty_child_collection()
    {
        var id = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => InsertAsync(id));

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);
            var doc = (await query.Where(d => d.Id == id).ToListAsync()).Single();

            // 空集合 → App 层组装出 null（与旧 JSON 列"未抽取时 null"语义一致）。
            doc.ExtractedFieldValues.ShouldBeEmpty();
        });
    }

    private async Task InsertAsync(Guid id, params DocumentFieldValue[] fields)
    {
        // FK RESTRICT 真实生效（#207）：先 seed 父 DocumentType + FieldDefinition 行（字段名仅占位，本测试按 FieldDefinitionId 断言）。
        await _documentTypeRepository.InsertAsync(
            new DocumentType(TypeId(TypeCode), null, TypeCode, TypeCode), autoSave: true);
        foreach (var f in fields)
        {
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    f.FieldDefinitionId, null, TypeId(TypeCode),
                    name: "f" + f.FieldDefinitionId.ToString("N"),
                    displayName: "field", prompt: "extract", dataType: f.DataType),
                autoSave: true);
        }

        var doc = new Document(
            id,
            tenantId: null,
            originalFileBlobName: $"blobs/{id:N}.pdf",
            fileOrigin: new FileOrigin("test-user", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1024, "f.pdf"));
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId(TypeCode));
        if (fields.Length > 0)
        {
            doc.SetFields(fields);
        }
        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    // 每个 FieldDataType 的代表样本 + 往返断言。default 分支抛错 = 穷尽性绊线（加新枚举值必须在此补全）。
    private static (JsonElement Value, Action<JsonElement> AssertRoundTrip) SampleFor(FieldDataType dataType) => dataType switch
    {
        FieldDataType.String => (Json("Acme"), e => e.GetString().ShouldBe("Acme")),
        FieldDataType.Number => (Json(1000.50m), e => e.GetDecimal().ShouldBe(1000.50m)),
        FieldDataType.Boolean => (Json(true), e => e.GetBoolean().ShouldBeTrue()),
        FieldDataType.Date => (Json("2024-03-09"), e => e.GetString().ShouldBe("2024-03-09")),
        FieldDataType.DateTime => (Json("2024-03-09T13:45:00"), e => e.GetString().ShouldBe("2024-03-09T13:45:00")),
        _ => throw new ArgumentOutOfRangeException(
            nameof(dataType), dataType, "新增 FieldDataType 必须在此补样本 + 往返断言（#208 穷尽性绊线）。")
    };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
    private static Guid FieldIdFor(FieldDataType dataType) => FieldId(dataType.ToString());
    private static Guid TypeId(string typeCode) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
}
