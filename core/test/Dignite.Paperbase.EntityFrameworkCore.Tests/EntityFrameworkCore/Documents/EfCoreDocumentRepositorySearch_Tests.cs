using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// <see cref="EfCoreDocumentRepository.GetFieldMatchedIdsAsync"/> 的真实 EF 集成测试（SQLite）——字段架构 v2
/// / Issue #206 + #207。字段值改为 <see cref="DocumentExtractedField"/> 一等 child 的普通类型化列后，匹配查询是纯
/// EF Core LINQ（Documents-anchored + child EXISTS + 普通列比较），可在 SQLite 上端到端执行。
/// #207：内部按 <see cref="DocumentFieldQuery.FieldDefinitionId"/> / <see cref="Document.DocumentTypeId"/> 匹配
/// （不再按字段名 / TypeCode 字符串）；测试用 name → 稳定 Guid 派生保证文档字段值与查询一致。
/// 覆盖：各 DataType 等值 / 范围、多字段 AND、租户隔离、软删除、reclassify 整组替换、loud fail-closed。
/// </summary>
public class EfCoreDocumentRepositorySearch_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private const string TypeCode = "contract.general";

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly HashSet<Guid> _seededTypes = new();
    private readonly HashSet<Guid> _seededFields = new();

    public EfCoreDocumentRepositorySearch_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    // FK RESTRICT 在 SQLite 测试中真实生效（#207）：Document.DocumentTypeId → DocumentType，
    // DocumentExtractedField.FieldDefinitionId → FieldDefinition。插入文档 / 字段值前必须先 seed 父行。
    // 幂等（HashSet 去重）；字段定义名仅占位（搜索按 FieldDefinitionId 匹配，不按名）。
    private async Task EnsureSchemaAsync(string typeCode, IEnumerable<DocumentFieldValue> fields)
    {
        var typeId = TypeId(typeCode);
        if (_seededTypes.Add(typeId))
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, _currentTenant.Id, typeCode, typeCode), autoSave: true);
        }

        foreach (var f in fields)
        {
            if (_seededFields.Add(f.FieldDefinitionId))
            {
                await _fieldDefinitionRepository.InsertAsync(
                    new FieldDefinition(
                        f.FieldDefinitionId, _currentTenant.Id, typeId,
                        name: "f" + f.FieldDefinitionId.ToString("N"),
                        displayName: "field", prompt: "extract", dataType: f.DataType),
                    autoSave: true);
            }
        }
    }

    // ─── loud fail-closed（触达普通列比较前短路 / 拒绝） ─────────────────────────

    [Fact]
    public async Task Empty_field_queries_returns_empty()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), Array.Empty<DocumentFieldQuery>());

            ids.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Range_on_string_field_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("party", FieldDataType.String, min: "a", max: "z") }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange);
        });
    }

    [Fact]
    public async Task Range_on_boolean_field_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("active", FieldDataType.Boolean, min: "false", max: "true") }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange);
        });
    }

    [Fact]
    public async Task Value_not_matching_declared_type_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // Number 字段传 "abc" → 值无法解析为声明类型 → loud fail（不静默空）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("count", FieldDataType.Number, value: "abc") }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.InvalidValue);
        });
    }

    [Fact]
    public async Task Field_query_with_no_value_throws_fail_closed()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // 等值 / 区间全空是残缺查询——必须 loud fail，绝不退化成「该类型全捞」（纵深防御，非 DTO 校验路径）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("count", FieldDataType.Number) }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.InvalidValue);
        });
    }

    [Theory]
    [InlineData("2024-01-01T10:00:00+08:00")]   // 显式偏移
    [InlineData("2024-01-01T10:00:00Z")]        // UTC 'Z'
    public async Task DateTime_offset_bearing_value_throws(string offsetInput)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // 带时区的 DateTime 入参与存储侧 wall-clock 语义不一致 → 判脏入参 loud fail（不静默空）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("created", FieldDataType.DateTime, value: offsetInput) }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.InvalidValue);
        });
    }

    // ─── 各 DataType 等值 ───────────────────────────────────────────────────────

    [Fact]
    public async Task String_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("party", FieldDataType.String, "Acme"));
            await InsertDocumentAsync(Field("party", FieldDataType.String, "Globex"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.String, value: "Acme") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Boolean_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("active", FieldDataType.Boolean, true));
            await InsertDocumentAsync(Field("active", FieldDataType.Boolean, false));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("active", FieldDataType.Boolean, value: "true") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    // Number 统一了整数与小数（同落 NumberValue）：以下两组分别验证整数形与小数形 JSON 值都能往返 + 匹配。
    [Fact]
    public async Task Number_equality_matches_integer_value()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("count", FieldDataType.Number, 7L));
            await InsertDocumentAsync(Field("count", FieldDataType.Number, 9L));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("count", FieldDataType.Number, value: "7") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Number_equality_matches_decimal_value()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("amount", FieldDataType.Number, 123.45m));
            await InsertDocumentAsync(Field("amount", FieldDataType.Number, 999.99m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("amount", FieldDataType.Number, value: "123.45") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Date_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2024-01-15"));
            await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2024-02-20"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("signed_on", FieldDataType.Date, value: "2024-01-15") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task DateTime_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-01-15T10:30:00"));
            await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-01-15T18:45:00"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("created", FieldDataType.DateTime, value: "2024-01-15T10:30:00") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    // ─── 范围（含界）───────────────────────────────────────────────────────────

    [Fact]
    public async Task Number_range_matches_inclusive_integer_values()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("count", FieldDataType.Number, 100L));   // 下界（含）
            var mid = await InsertDocumentAsync(Field("count", FieldDataType.Number, 150L));
            await InsertDocumentAsync(Field("count", FieldDataType.Number, 250L));   // 越上界

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("count", FieldDataType.Number, min: "100", max: "200") });

            ids.Count.ShouldBe(2);
            ids.ShouldContain(mid);
        });
    }

    [Fact]
    public async Task Number_range_matches_inclusive_decimal_values()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // 同位数（3 整数位）取值——既验证数值区间逻辑，又规避 SQLite 把 decimal 存为 TEXT、按字典序比较的怪异
            // （生产 SQL Server 是真 decimal 列、数值比较）。代码路径与整数值 Number / Date / DateTime 区间完全一致。
            await InsertDocumentAsync(Field("amount", FieldDataType.Number, 200m));
            var mid = await InsertDocumentAsync(Field("amount", FieldDataType.Number, 300m));
            await InsertDocumentAsync(Field("amount", FieldDataType.Number, 400m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("amount", FieldDataType.Number, min: "250", max: "350") });

            ids.ShouldHaveSingleItem().ShouldBe(mid);
        });
    }

    [Fact]
    public async Task Date_range_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2023-12-31"));
            var mid = await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2024-06-15"));
            await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2025-01-01"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("signed_on", FieldDataType.Date, min: "2024-01-01", max: "2024-12-31") });

            ids.ShouldHaveSingleItem().ShouldBe(mid);
        });
    }

    [Fact]
    public async Task DateTime_range_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-01-01T00:00:00"));
            var mid = await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-06-15T12:00:00"));
            await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2025-01-01T00:00:00"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[]
                {
                    Query("created", FieldDataType.DateTime, min: "2024-01-02T00:00:00", max: "2024-12-31T23:59:59")
                });

            ids.ShouldHaveSingleItem().ShouldBe(mid);
        });
    }

    // ─── 多字段 AND ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Multiple_field_filters_are_ANDed()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var both = await InsertDocumentAsync(
                Field("party", FieldDataType.String, "Acme"),
                Field("amount", FieldDataType.Number, 300m));
            // 只满足一个条件 → 不应命中（AND，不同字段互相收窄）。
            await InsertDocumentAsync(
                Field("party", FieldDataType.String, "Acme"),
                Field("amount", FieldDataType.Number, 999m));
            await InsertDocumentAsync(
                Field("party", FieldDataType.String, "Globex"),
                Field("amount", FieldDataType.Number, 300m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[]
                {
                    Query("party", FieldDataType.String, value: "Acme"),
                    Query("amount", FieldDataType.Number, min: "250", max: "350")
                });

            ids.ShouldHaveSingleItem().ShouldBe(both);
        });
    }

    // ─── 锚定文档类型 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Field_match_anchors_to_document_type()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await InsertDocumentAsync(TypeCode, Field("party", FieldDataType.String, "Acme"));
            // 同字段值但不同类型——不应命中（查询锚定单一 documentTypeId）。
            await InsertDocumentAsync("invoice.general", Field("party", FieldDataType.String, "Acme"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.String, value: "Acme") });

            ids.ShouldHaveSingleItem().ShouldBe(contract);
        });
    }

    // ─── 软删除隔离（沿用 Document 聚合根的 ISoftDelete 全局过滤器） ──────────────

    [Fact]
    public async Task Soft_deleted_documents_are_excluded_by_default()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var id = await InsertDocumentAsync(Field("party", FieldDataType.String, "Acme"));
            await _documentRepository.DeleteAsync(id, autoSave: true); // 软删

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.String, value: "Acme") });

            ids.ShouldBeEmpty();

            // 回收站语义：在 Disable<ISoftDelete> 作用域内（GetListAsync 的 IsDeleted 路径）应能命中。
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var deletedIds = await _documentRepository.GetFieldMatchedIdsAsync(
                    TypeId(TypeCode), new[] { Query("party", FieldDataType.String, value: "Acme") });
                deletedIds.ShouldHaveSingleItem().ShouldBe(id);
            }
        });
    }

    // ─── 租户隔离（沿用 Document 聚合根的 IMultiTenant 全局过滤器，不手写 TenantId 谓词） ──

    [Fact]
    public async Task Tenant_isolation_is_enforced_by_document_filter()
    {
        var tenantId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            Guid tenantDocId;
            using (_currentTenant.Change(tenantId))
            {
                tenantDocId = await InsertDocumentAsync(Field("party", FieldDataType.String, "Acme"));
            }

            // Host 上下文（CurrentTenant.Id == null）查不到租户文档。
            var hostIds = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.String, value: "Acme") });
            hostIds.ShouldBeEmpty();

            // 切到该租户上下文即可查到。
            using (_currentTenant.Change(tenantId))
            {
                var tenantIds = await _documentRepository.GetFieldMatchedIdsAsync(
                    TypeId(TypeCode), new[] { Query("party", FieldDataType.String, value: "Acme") });
                tenantIds.ShouldHaveSingleItem().ShouldBe(tenantDocId);
            }
        });
    }

    // ─── 整组替换 / 原地更新（reconcile） ───────────────────────────────────────

    [Fact]
    public async Task Reclassify_replaces_whole_field_set()
    {
        Guid id = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var amount = Field("amount", FieldDataType.Number, 100m);
            await EnsureSchemaAsync(TypeCode, new[] { amount });
            await _documentRepository.InsertAsync(
                CreateDocument(id, _currentTenant.Id, TypeCode, amount),
                autoSave: true);
        });

        // reclassify 到新类型 + 新字段集（含 details 加载现有字段行供 reconcile diff）。
        await WithUnitOfWorkAsync(async () =>
        {
            var total = Field("total", FieldDataType.Number, 200m);
            await EnsureSchemaAsync("invoice.general", new[] { total });
            var doc = await _documentRepository.GetAsync(id, includeDetails: true);
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId("invoice.general"));
            doc.SetFields(new[] { total });
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(id, includeDetails: true);

            // 旧 schema 字段（amount）不残留；只剩新 schema 字段（total，按 FieldDefinitionId 匹配）。
            reloaded.ExtractedFieldValues.Select(f => f.FieldDefinitionId).ShouldBe(new[] { FieldId("total") });

            // 旧字段已查不到（锚定旧类型也查不到旧字段值）。
            var oldHits = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("amount", FieldDataType.Number, value: "100") });
            oldHits.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Same_name_field_is_updated_in_place()
    {
        Guid id = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var amount = Field("amount", FieldDataType.Number, 100m);
            await EnsureSchemaAsync(TypeCode, new[] { amount });
            await _documentRepository.InsertAsync(
                CreateDocument(id, _currentTenant.Id, TypeCode, amount),
                autoSave: true);
        });

        // 操作员手改：同字段值改 100 → 200（复合键 (DocumentId, FieldDefinitionId) 下 reconcile 原地更新，不产生重复行 / PK 冲突）。
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(id, includeDetails: true);
            doc.SetFields(new[] { Field("amount", FieldDataType.Number, 200m) });
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(id, includeDetails: true);
            reloaded.ExtractedFieldValues.ShouldHaveSingleItem().NumberValue.ShouldBe(200m);

            var newHits = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("amount", FieldDataType.Number, value: "200") });
            newHits.ShouldHaveSingleItem().ShouldBe(id);
        });
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private Task<Guid> InsertDocumentAsync(params DocumentFieldValue[] fields)
        => InsertDocumentAsync(TypeCode, fields);

    private async Task<Guid> InsertDocumentAsync(string typeCode, params DocumentFieldValue[] fields)
    {
        await EnsureSchemaAsync(typeCode, fields);
        var id = Guid.NewGuid();
        await _documentRepository.InsertAsync(
            CreateDocument(id, _currentTenant.Id, typeCode, fields), autoSave: true);
        return id;
    }

    private static Document CreateDocument(Guid id, Guid? tenantId, string typeCode, params DocumentFieldValue[] fields)
    {
        var doc = new Document(
            id,
            tenantId,
            originalFileBlobName: $"blobs/{id:N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "f.pdf"));

        // DocumentTypeId 为 Domain private setter——测试用反射模拟"已分类"（#207 内部按 Id 关联）。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId(typeCode));

        if (fields.Length > 0)
        {
            doc.SetFields(fields);
        }

        return doc;
    }

    private static DocumentFieldValue Field<T>(string name, FieldDataType dataType, T value)
        => new(FieldId(name), dataType, JsonSerializer.SerializeToElement(value));

    private static DocumentFieldQuery Query(
        string name, FieldDataType dataType, string? value = null, string? min = null, string? max = null)
        => new(FieldId(name), name, dataType, value, min, max);

    // name / typeCode → 稳定 Guid 派生（#207：内部按 Id 匹配；同名在文档字段值与查询间一致）。
    private static Guid FieldId(string name) => DeterministicGuid("field:" + name);
    private static Guid TypeId(string typeCode) => DeterministicGuid("type:" + typeCode);

    private static Guid DeterministicGuid(string key)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
