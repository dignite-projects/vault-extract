using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// #527 §4: EF mapping for the <see cref="DocumentFieldValidationWarning"/> child collection against the real SQLite
/// DB — composite key <c>(DocumentId, FieldDefinitionId)</c> round-trip, the tenant + message columns, and cascade
/// delete when the owning <see cref="Document"/> is hard-deleted. The FK to <see cref="FieldDefinition"/> is RESTRICT
/// (mirrors <see cref="DocumentExtractedField"/>), so the parent <see cref="DocumentType"/> + <see cref="FieldDefinition"/>
/// rows are seeded first. Mirrors <c>DocumentReadAssembly_Tests</c>.
/// </summary>
public class DocumentFieldValidationWarningPersistence_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private const string TypeCode = "host.bank-statement";

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDbContextProvider<VaultExtractDbContext> _dbContextProvider;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentFieldValidationWarningPersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _dbContextProvider = GetRequiredService<IDbContextProvider<VaultExtractDbContext>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Warnings_round_trip_by_composite_key_with_message_and_tenant()
    {
        var id = _guidGenerator.Create();
        var transactions = FieldId("transactions");
        var balance = FieldId("balance");

        await WithUnitOfWorkAsync(() => InsertAsync(id, new[]
        {
            new FieldValidationWarning(transactions, "Row 4 balance does not reconcile with the prior row."),
            new FieldValidationWarning(balance, "Closing balance does not equal opening plus net movement.")
        }, transactions, balance));

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.WithDetailsAsync(d => d.FieldValidationWarnings);
            var doc = (await query.Where(d => d.Id == id).ToListAsync()).Single();

            var byField = doc.FieldValidationWarnings.ToDictionary(w => w.FieldDefinitionId, w => w);
            byField.Count.ShouldBe(2);
            byField[transactions].Message.ShouldBe("Row 4 balance does not reconcile with the prior row.");
            byField[balance].Message.ShouldBe("Closing balance does not equal opening plus net movement.");
            byField[transactions].DocumentId.ShouldBe(id);
            byField[transactions].TenantId.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Hard_deleting_the_document_cascades_the_warning_rows()
    {
        var id = _guidGenerator.Create();
        var transactions = FieldId("transactions");

        await WithUnitOfWorkAsync(() => InsertAsync(id,
            new[] { new FieldValidationWarning(transactions, "mismatch") }, transactions));

        (await CountWarningsAsync(id)).ShouldBe(1);

        await WithUnitOfWorkAsync(() => _documentRepository.HardDeleteAsync(id));

        (await CountWarningsAsync(id)).ShouldBe(0);
    }

    [Fact]
    public async Task Reclassify_loads_and_deletes_persisted_warning_rows_no_orphans()
    {
        // #527 §7 + the load-path fix: a document with PERSISTED warning rows, when reclassified, must LOAD its warnings
        // (via FindWithFieldValuesAsync — the field-stage loader) so ClearFieldValidationWarnings actually DELETES the DB
        // rows instead of clearing the blocking bit while orphaning them. Without the FieldValidationWarnings Include in
        // FindWithFieldValuesAsync, the load below hydrates 0 warnings and the delete never happens.
        var id = _guidGenerator.Create();
        var transactions = FieldId("transactions");
        var targetType = TypeId("host.reclassify-target");

        await WithUnitOfWorkAsync(async () =>
        {
            await InsertAsync(id, new[] { new FieldValidationWarning(transactions, "mismatch") }, transactions);
            // Seed the reclassify target type so the Document -> DocumentType FK (Restrict) is satisfied on save.
            await _documentTypeRepository.InsertAsync(
                new DocumentType(targetType, null, "host.reclassify-target", "target"), autoSave: true);
        });

        (await CountWarningsAsync(id)).ShouldBe(1);

        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.FindWithFieldValuesAsync(id);
            doc.ShouldNotBeNull();
            // The field-stage loader now hydrates the warnings collection (the load-path fix under test).
            doc!.FieldValidationWarnings.Count.ShouldBe(1);

            // Reclassify to another type -> §7 ClearFieldValidationWarnings (internal; invoked via reflection).
            ApplyAutomaticClassificationResult(doc, targetType, 0.95);
            doc.FieldValidationWarnings.ShouldBeEmpty();

            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        // The DB rows are gone — no orphans; the collection and the blocking bit stay consistent at the DB level.
        (await CountWarningsAsync(id)).ShouldBe(0);
    }

    private async Task<int> CountWarningsAsync(Guid documentId)
    {
        var count = 0;
        await WithUnitOfWorkAsync(async () =>
        {
            var context = await _dbContextProvider.GetDbContextAsync();
            count = await context.Set<DocumentFieldValidationWarning>()
                .CountAsync(w => w.DocumentId == documentId);
        });
        return count;
    }

    private async Task InsertAsync(Guid id, FieldValidationWarning[] warnings, params Guid[] fieldIds)
    {
        // FK RESTRICT to FieldDefinition (#207): seed the parent DocumentType + FieldDefinition rows first.
        await _documentTypeRepository.InsertAsync(
            new DocumentType(TypeId(TypeCode), null, TypeCode, TypeCode), autoSave: true);
        foreach (var fieldId in fieldIds)
        {
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    fieldId, null, TypeId(TypeCode),
                    name: "f" + fieldId.ToString("N"),
                    displayName: "field", prompt: "extract", dataType: FieldDataType.Text),
                autoSave: true);
        }

        var doc = new Document(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin($"blobs/{id:N}.pdf", "test-user", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1024, "f.pdf"));
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId(TypeCode));
        doc.ReplaceFieldValidationWarnings(warnings);
        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    // The classification-outcome transitions are internal (called by DocumentPipelineRunManager); Application/EF tests
    // have no InternalsVisibleTo, so invoke via reflection (same pattern as the Domain transition-matrix tests).
    private static void ApplyAutomaticClassificationResult(Document doc, Guid documentTypeId, double confidence)
        => typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [documentTypeId, confidence]);

    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
    private static Guid TypeId(string typeCode) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
}
