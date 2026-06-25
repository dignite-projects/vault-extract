using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration tests (SQLite) for <see cref="EfCoreDocumentRepository.GetFieldMatchedIdsAsync"/>, covering
/// field schema v2 / Issues #206 and #207. After field values moved to first-class
/// <see cref="DocumentExtractedField"/> child rows with normal typed columns, matched queries are pure EF Core
/// LINQ (Documents-anchored + child EXISTS + normal column comparison), executable end to end on SQLite.
/// #207: internal matching uses <see cref="DocumentFieldQuery.FieldDefinitionId"/> /
/// <see cref="Document.DocumentTypeId"/>, no longer field-name / TypeCode strings. Tests derive stable Guids
/// from name to keep document field values and queries consistent.
/// Coverage: equality / range for each DataType, multi-field AND, tenant isolation, soft delete,
/// reclassification whole-set replacement, and loud fail-closed behavior.
/// </summary>
public class EfCoreDocumentRepositorySearch_Tests : ExtractEntityFrameworkCoreTestBase
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

    // FK RESTRICT is truly enforced in SQLite tests (#207): Document.DocumentTypeId -> DocumentType and
    // DocumentExtractedField.FieldDefinitionId -> FieldDefinition. Parent rows must be seeded before inserting
    // documents / field values. Idempotent through HashSet de-duplication; field definition names are placeholders
    // because search matches by FieldDefinitionId, not by name.
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

    // --- loud fail-closed: short-circuit / reject before normal column comparison ---

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
                new[] { Query("party", FieldDataType.Text, min: "a", max: "z") }));

            ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange);
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

            ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange);
        });
    }

    [Fact]
    public async Task Value_not_matching_declared_type_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // Passing "abc" to a Number field cannot be parsed as the declared type -> loud fail, not silent empty.
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("count", FieldDataType.Number, value: "abc") }));

            ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.InvalidValue);
        });
    }

    [Fact]
    public async Task Field_query_with_no_value_throws_fail_closed()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // Empty equality / range query is incomplete and must loud fail. It must never degrade to "fetch all
            // documents of this type"; this is defense in depth, not the DTO validation path.
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("count", FieldDataType.Number) }));

            ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.InvalidValue);
        });
    }

    [Theory]
    [InlineData("2024-01-01T10:00:00+08:00")]   // Explicit offset.
    [InlineData("2024-01-01T10:00:00Z")]        // UTC 'Z'
    public async Task DateTime_offset_bearing_value_throws(string offsetInput)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // DateTime input with a time zone conflicts with storage-side wall-clock semantics -> treat as dirty
            // input and loud fail, not silent empty.
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode),
                new[] { Query("created", FieldDataType.DateTime, value: offsetInput) }));

            ex.Code.ShouldBe(ExtractErrorCodes.ExtractedField.InvalidValue);
        });
    }

    // --- equality for each DataType ---

    [Fact]
    public async Task String_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("party", FieldDataType.Text, "Acme"));
            await InsertDocumentAsync(Field("party", FieldDataType.Text, "Globex"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.Text, value: "Acme") });

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

    // Number unifies integers and decimals in NumberValue. The next two groups separately verify that integer
    // and decimal JSON values both round-trip and match.
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

    // --- inclusive ranges ---

    [Fact]
    public async Task Number_range_matches_inclusive_integer_values()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("count", FieldDataType.Number, 100L));   // Lower bound, inclusive.
            var mid = await InsertDocumentAsync(Field("count", FieldDataType.Number, 150L));
            await InsertDocumentAsync(Field("count", FieldDataType.Number, 250L));   // Above upper bound.

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
            // Use same-width values (3 integer digits) to verify numeric range logic while avoiding SQLite's odd
            // decimal-as-TEXT lexicographic comparison. Production SQL Server uses a real decimal column and
            // numeric comparison. This code path is identical to integer Number / Date / DateTime ranges.
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

    // --- multi-field AND ---

    [Fact]
    public async Task Multiple_field_filters_are_ANDed()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var both = await InsertDocumentAsync(
                Field("party", FieldDataType.Text, "Acme"),
                Field("amount", FieldDataType.Number, 300m));
            // Satisfies only one condition -> should not match. AND narrows across different fields.
            await InsertDocumentAsync(
                Field("party", FieldDataType.Text, "Acme"),
                Field("amount", FieldDataType.Number, 999m));
            await InsertDocumentAsync(
                Field("party", FieldDataType.Text, "Globex"),
                Field("amount", FieldDataType.Number, 300m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[]
                {
                    Query("party", FieldDataType.Text, value: "Acme"),
                    Query("amount", FieldDataType.Number, min: "250", max: "350")
                });

            ids.ShouldHaveSingleItem().ShouldBe(both);
        });
    }

    // --- anchored document type ---

    [Fact]
    public async Task Field_match_anchors_to_document_type()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await InsertDocumentAsync(TypeCode, Field("party", FieldDataType.Text, "Acme"));
            // Same field value but different type should not match because the query is anchored to one documentTypeId.
            await InsertDocumentAsync("invoice.general", Field("party", FieldDataType.Text, "Acme"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.Text, value: "Acme") });

            ids.ShouldHaveSingleItem().ShouldBe(contract);
        });
    }

    // --- soft-delete isolation using the Document aggregate root ISoftDelete global filter ---

    [Fact]
    public async Task Soft_deleted_documents_are_excluded_by_default()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var id = await InsertDocumentAsync(Field("party", FieldDataType.Text, "Acme"));
            await _documentRepository.DeleteAsync(id, autoSave: true); // Soft delete.

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.Text, value: "Acme") });

            ids.ShouldBeEmpty();

            // Trash semantics: inside a Disable<ISoftDelete> scope, the GetListAsync IsDeleted path should match.
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var deletedIds = await _documentRepository.GetFieldMatchedIdsAsync(
                    TypeId(TypeCode), new[] { Query("party", FieldDataType.Text, value: "Acme") });
                deletedIds.ShouldHaveSingleItem().ShouldBe(id);
            }
        });
    }

    // --- tenant isolation using the Document aggregate root IMultiTenant global filter, not handwritten TenantId predicates ---

    [Fact]
    public async Task Tenant_isolation_is_enforced_by_document_filter()
    {
        var tenantId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            Guid tenantDocId;
            using (_currentTenant.Change(tenantId))
            {
                tenantDocId = await InsertDocumentAsync(Field("party", FieldDataType.Text, "Acme"));
            }

            // Host context (CurrentTenant.Id == null) cannot see tenant documents.
            var hostIds = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("party", FieldDataType.Text, value: "Acme") });
            hostIds.ShouldBeEmpty();

            // Switching to that tenant context makes it visible.
            using (_currentTenant.Change(tenantId))
            {
                var tenantIds = await _documentRepository.GetFieldMatchedIdsAsync(
                    TypeId(TypeCode), new[] { Query("party", FieldDataType.Text, value: "Acme") });
                tenantIds.ShouldHaveSingleItem().ShouldBe(tenantDocId);
            }
        });
    }

    // --- multi-value text fields (#212) ---

    [Fact]
    public async Task Multi_value_string_field_persists_ordered_rows_and_matches_any_value()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // Multi-value fields use multiple rows for one field, with Order in the composite key. AllowMultiple
            // is the App-layer expansion gate; the EF storage / query layer only sees rows. Construct three value
            // rows with the same FieldDefinitionId and different Order directly to verify storage + query mechanics.
            var tagsId = FieldId("tags");
            var values = new[]
            {
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("urgent"), 0),
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("legal"), 1),
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("2026"), 2),
            };
            var id = await InsertDocumentAsync(values);

            // Three rows persist and are restored by Order.
            var doc = await _documentRepository.GetAsync(id, includeDetails: true);
            doc.ExtractedFieldValues.Count.ShouldBe(3);
            doc.ExtractedFieldValues.OrderBy(f => f.Order).Select(f => f.TextValue)
                .ShouldBe(new[] { "urgent", "legal", "2026" });

            // Match by any value. .Any spans rows, so the query path is naturally compatible with multi-values.
            var byMiddle = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("tags", FieldDataType.Text, value: "legal") });
            byMiddle.ShouldHaveSingleItem().ShouldBe(id);

            var byLast = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("tags", FieldDataType.Text, value: "2026") });
            byLast.ShouldHaveSingleItem().ShouldBe(id);

            // Values that were not extracted do not match.
            var miss = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("tags", FieldDataType.Text, value: "archived") });
            miss.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Multi_value_field_shrinks_via_reconcile_without_orphan_rows()
    {
        Guid id = Guid.NewGuid();
        var tagsId = FieldId("tags");

        await WithUnitOfWorkAsync(async () =>
        {
            var values = new[]
            {
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("a"), 0),
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("b"), 1),
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("c"), 2),
            };
            await EnsureSchemaAsync(TypeCode, values);
            await _documentRepository.InsertAsync(
                CreateDocument(id, _currentTenant.Id, TypeCode, values), autoSave: true);
        });

        // ["a","b","c"] -> ["x","y"]: Order 0/1 are updated in place and Order 2 is deleted, reconciling without
        // key collisions or orphan rows.
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(id, includeDetails: true);
            doc.SetFields(new[]
            {
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("x"), 0),
                new DocumentFieldValue(tagsId, FieldDataType.Text, JsonSerializer.SerializeToElement("y"), 1),
            });
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(id, includeDetails: true);
            reloaded.ExtractedFieldValues.OrderBy(f => f.Order).Select(f => f.TextValue)
                .ShouldBe(new[] { "x", "y" });

            // The deleted old Order 2 value ("c") cannot be found.
            var oldHits = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeId(TypeCode), new[] { Query("tags", FieldDataType.Text, value: "c") });
            oldHits.ShouldBeEmpty();
        });
    }

    // --- whole-set replacement / in-place update (reconcile) ---

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

        // Reclassify to a new type + new field set, including details to load existing field rows for reconcile diff.
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

            // Old-schema field (amount) does not remain; only the new-schema field (total) remains, matched by
            // FieldDefinitionId.
            reloaded.ExtractedFieldValues.Select(f => f.FieldDefinitionId).ShouldBe(new[] { FieldId("total") });

            // Old field can no longer be found; even anchoring to the old type cannot find the old field value.
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

        // Operator manual edit: same field changes 100 -> 200. Under composite key
        // (DocumentId, FieldDefinitionId, Order), single-value field Order 0 reconciles in place without duplicate
        // rows or PK conflicts.
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
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "f.pdf"));

        // DocumentTypeId has a Domain private setter; tests use reflection to simulate the classified state
        // (#207 internal relation by Id).
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

    // name / typeCode -> stable Guid derivation (#207: internal matching by Id; same name is consistent between
    // document field values and queries).
    private static Guid FieldId(string name) => DeterministicGuid("field:" + name);
    private static Guid TypeId(string typeCode) => DeterministicGuid("type:" + typeCode);

    private static Guid DeterministicGuid(string key)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
