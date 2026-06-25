using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// The exit DTO wire format for <c>ExtractedFields</c> is assembled just in time by the App / Mapper layer from
/// typed <see cref="DocumentExtractedField"/> child rows (Issue #206 + #207). This test uses real EF (SQLite) to
/// verify two read-path mechanisms:
/// <list type="bullet">
///   <item><c>WithDetailsAsync(selector)</c>: the ABP repository API used by <c>GetListAsync</c> to eager-load
///   child rows. One JOIN fetches them without lazy loading; lazy loading is disabled in both tests and
///   production, so this passing test proves assembly does not trigger N+1 or lazy loading.</item>
///   <item><see cref="DocumentExtractedField.ToJsonElement"/>: rebuilds each DataType's typed column into
///   canonical JSON, round-tripping consistently with write-side <c>SetValue</c>.</item>
/// </list>
/// #207: child rows are indexed internally by <see cref="DocumentExtractedField.FieldDefinitionId"/>, no longer
/// by field name. Exit dictionary keys, field names, are resolved by the App-layer join to the current
/// <c>FieldDefinition</c>. Name-resolution wiring is covered by Application.Tests; this test only verifies the
/// typed-column round-trip, so assertions use FieldDefinitionId keys.
/// </summary>
public class DocumentReadAssembly_Tests : ExtractEntityFrameworkCoreTestBase
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
        // Exhaustiveness tripwire (#208): iterate every FieldDataType. Adding a new enum value without a SampleFor
        // sample throws while building samples. After a sample is added, round-trip passes through SetValue and
        // ToJsonElement, so missing handling in either typed-column switch also fails.
        var dataTypes = Enum.GetValues<FieldDataType>();
        var samples = dataTypes.ToDictionary(dt => dt, SampleFor);

        var id = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => InsertAsync(id,
            dataTypes.Select(dt => new DocumentFieldValue(FieldIdFor(dt), dt, samples[dt].Value)).ToArray()));

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);
            var doc = (await query.Where(d => d.Id == id).ToListAsync()).Single();

            // #208: field type is decided by FieldDefinition and is not persisted in field-value rows; the read
            // path joins DataType and rebuilds JSON.
            var types = (await _fieldDefinitionRepository.GetListAsync()).ToDictionary(f => f.Id, f => f.DataType);

            // typed-column -> canonical JSON round-trip. Keys use FieldDefinitionId; field-name resolution is an
            // App-layer join and is not asserted at this layer.
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

            // Empty collection -> App layer assembles null, matching the old JSON column semantics where
            // unextracted fields were null.
            doc.ExtractedFieldValues.ShouldBeEmpty();
        });
    }

    private async Task InsertAsync(Guid id, params DocumentFieldValue[] fields)
    {
        // FK RESTRICT is truly enforced (#207): seed parent DocumentType + FieldDefinition rows first. Field names
        // are placeholders because this test asserts by FieldDefinitionId.
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
            fileOrigin: new FileOrigin($"blobs/{id:N}.pdf", "test-user", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1024, "f.pdf"));
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId(TypeCode));
        if (fields.Length > 0)
        {
            doc.SetFields(fields);
        }
        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    // Representative sample + round-trip assertion for each FieldDataType. The default branch throws as an
    // exhaustiveness tripwire: new enum values must be added here.
    private static (JsonElement Value, Action<JsonElement> AssertRoundTrip) SampleFor(FieldDataType dataType) => dataType switch
    {
        FieldDataType.Text => (Json("Acme"), e => e.GetString().ShouldBe("Acme")),
        FieldDataType.LongText => (Json("A longer free-form passage that spans a sentence or two."),
            e => e.GetString().ShouldBe("A longer free-form passage that spans a sentence or two.")),
        FieldDataType.Number => (Json(1000.50m), e => e.GetDecimal().ShouldBe(1000.50m)),
        FieldDataType.Boolean => (Json(true), e => e.GetBoolean().ShouldBeTrue()),
        FieldDataType.Date => (Json("2024-03-09"), e => e.GetString().ShouldBe("2024-03-09")),
        FieldDataType.DateTime => (Json("2024-03-09T13:45:00"), e => e.GetString().ShouldBe("2024-03-09T13:45:00")),
        _ => throw new ArgumentOutOfRangeException(
            nameof(dataType), dataType, "New FieldDataType values must add a sample and round-trip assertion here (#208 exhaustiveness tripwire).")
    };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
    private static Guid FieldIdFor(FieldDataType dataType) => FieldId(dataType.ToString());
    private static Guid TypeId(string typeCode) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
}
