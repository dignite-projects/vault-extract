using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.Exports;

/// <summary>
/// #499 (carried over from #414 / #496): real EF (SQLite) coverage that <see cref="DocumentExportAppService"/>
/// narrows by exactly the filters the operator document list can express — extracted field values, the review
/// queue (<c>HasReviewReasons</c>, #284 / #395), and sub-document provenance (<c>OriginDocumentId</c>, #354) — so
/// "download current view" downloads the view rather than a broader set.
/// <para>
/// Two ways that guarantee could silently rot are pinned here: the review filter must run the canonical
/// <c>DocumentReviewQueries.RequiresAttention</c> predicate (a rejected document has already been handled and must
/// stay out), and an unknown field name must loud-fail rather than silently matching nothing.
/// </para>
/// </summary>
public class DocumentExportAppService_Filter_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private const string TypeCode = "invoice.general";

    private readonly IDocumentExportAppService _exportAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICurrentTenant _currentTenant;

    public DocumentExportAppService_Filter_Tests()
    {
        _exportAppService = GetRequiredService<IDocumentExportAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task Export_without_filters_includes_every_document_of_the_type()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m);
            await SeedDocumentAsync(200m);
        });

        var csv = await ExportCsvAsync(NewInput());

        // Baseline: proves the filters below are what narrow the result.
        csv.ShouldContain("100");
        csv.ShouldContain("200");
    }

    [Fact]
    public async Task Export_narrows_documents_by_field_filter()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m);
            await SeedDocumentAsync(200m);
        });

        var csv = await ExportCsvAsync(NewInput(i =>
            i.FieldFilters = new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } }));

        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    [Fact]
    public async Task Export_with_an_unknown_field_loud_fails()
    {
        await WithUnitOfWorkAsync(SeedSchemaAsync);

        var ex = await Should.ThrowAsync<BusinessException>(() => _exportAppService.ExportAsync(NewInput(i =>
            i.FieldFilters = new List<DocumentFieldFilter> { new() { Name = "ghost", Value = "x" } })));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);
    }

    [Fact]
    public async Task Export_narrows_to_documents_requiring_review()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            await SeedDocumentAsync(200m);
        });

        var csv = await ExportCsvAsync(NewInput(i => i.HasReviewReasons = true));

        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    [Fact]
    public async Task Export_review_filter_excludes_a_rejected_document()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            // Same unresolved reason, but the operator already rejected it — RequiresAttention drops it from the
            // queue. A hand-rolled `ReviewReasons != None` predicate would wrongly export this row, which is the
            // whole reason the export reuses DocumentReviewQueries.RequiresAttention instead of re-deriving it.
            await SeedDocumentAsync(200m, d =>
            {
                d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true);
                d.RejectReview("not a real invoice");
            });
        });

        var csv = await ExportCsvAsync(NewInput(i => i.HasReviewReasons = true));

        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    [Fact]
    public async Task Export_narrows_to_the_sub_documents_of_a_source()
    {
        var containerId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, id: containerId);
            await SeedDerivedDocumentAsync(200m, containerId, "seg-1");
            await SeedDerivedDocumentAsync(300m, containerId, "seg-2");
            // An unrelated top-level document of the same type: must not ride along.
            await SeedDocumentAsync(400m);
        });

        var csv = await ExportCsvAsync(NewInput(i => i.OriginDocumentId = containerId));

        csv.ShouldContain("200");
        csv.ShouldContain("300");
        csv.ShouldNotContain("100");
        csv.ShouldNotContain("400");
    }

    [Fact]
    public async Task Export_and_combines_the_review_filter_with_a_field_filter()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            // Needs review, but the field filter excludes it.
            await SeedDocumentAsync(200m, d => d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, true));
            // Matches the field filter, but needs no review.
            await SeedDocumentAsync(300m);
        });

        var csv = await ExportCsvAsync(NewInput(i =>
        {
            i.HasReviewReasons = true;
            i.FieldFilters = new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } };
        }));

        // AND, not OR: only the row satisfying both survives.
        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
        csv.ShouldNotContain("300");
    }

    [Fact]
    public async Task Field_filter_resolution_falls_back_to_the_repository_when_the_preloaded_cache_misses()
    {
        // #501 item 4: the export hands its already-loaded field definitions to DocumentFieldQueryResolver so a
        // filtered name costs no extra round-trip. The cache is matched with StringComparison.Ordinal; the
        // repository compares in SQL, where the column collation decides — SQL Server's default is
        // case-INsensitive. Loud-failing on an ordinal miss would therefore reject a name the database, and the
        // list path that has no cache, still accept: a fresh file-vs-screen divergence, which is the one thing
        // #501 item 1 exists to prevent. Only the database gets to say a field does not exist.
        //
        // An empty cache stands in for the miss, because SQLite cannot be made case-insensitive here. Delete the
        // `?? await fieldDefinitionRepository.FindByNameAsync(...)` fallback and this reddens.
        await WithUnitOfWorkAsync(SeedSchemaAsync);

        await WithUnitOfWorkAsync(async () =>
        {
            var queries = await DocumentFieldQueryResolver.ResolveAsync(
                _fieldDefinitionRepository,
                new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } },
                TypeId,
                TypeCode,
                knownDefinitions: new List<FieldDefinition>());

            queries.ShouldHaveSingleItem().FieldDefinitionId.ShouldBe(AmountFieldId);
        });
    }

    [Fact]
    public async Task Field_filter_resolution_uses_the_preloaded_definition_when_it_hits()
    {
        // The hit path must resolve to the same FieldDefinitionId the repository would have returned — the cache
        // is an optimisation, never a second source of truth.
        await WithUnitOfWorkAsync(SeedSchemaAsync);

        await WithUnitOfWorkAsync(async () =>
        {
            var definitions = await _fieldDefinitionRepository.GetListAsync(TypeId);

            var queries = await DocumentFieldQueryResolver.ResolveAsync(
                _fieldDefinitionRepository,
                new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } },
                TypeId,
                TypeCode,
                knownDefinitions: definitions);

            queries.ShouldHaveSingleItem().FieldDefinitionId.ShouldBe(AmountFieldId);
        });
    }

    private static ExportDocumentsInput NewInput(Action<ExportDocumentsInput>? configure = null)
    {
        var input = new ExportDocumentsInput { DocumentTypeCode = TypeCode };
        configure?.Invoke(input);
        return input;
    }

    private async Task<string> ExportCsvAsync(ExportDocumentsInput input)
    {
        var content = await _exportAppService.ExportAsync(input);
        using var reader = new StreamReader(content.GetStream());
        return await reader.ReadToEndAsync();
    }

    private async Task SeedSchemaAsync()
    {
        await _documentTypeRepository.InsertAsync(
            new DocumentType(TypeId, _currentTenant.Id, TypeCode, TypeCode), autoSave: true);
        await _fieldDefinitionRepository.InsertAsync(
            new FieldDefinition(
                AmountFieldId, _currentTenant.Id, TypeId,
                name: "amount", displayName: "Amount", prompt: null, dataType: FieldDataType.Number),
            autoSave: true);
    }

    private Task SeedDocumentAsync(decimal amount, Action<Document>? configure = null, Guid? id = null)
    {
        var documentId = id ?? Guid.NewGuid();
        var doc = new Document(documentId, _currentTenant.Id, NewFileOrigin(documentId));
        return PersistAsync(doc, amount, configure);
    }

    private Task SeedDerivedDocumentAsync(decimal amount, Guid originDocumentId, string constituentKey)
    {
        var documentId = Guid.NewGuid();
        // A sub-document has no file of its own (#487 reverted FileOrigin to nullable); it is a Markdown slice
        // reached through OriginDocumentId.
        var doc = Document.CreateDerived(documentId, _currentTenant.Id, fileOrigin: null, originDocumentId, constituentKey);
        return PersistAsync(doc, amount, configure: null);
    }

    private async Task PersistAsync(Document doc, decimal amount, Action<Document>? configure)
    {
        // DocumentTypeId has a Domain private setter; simulate the classified state (#207 internal relation by Id).
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId);
        doc.SetFields(new[]
        {
            new DocumentFieldValue(AmountFieldId, FieldDataType.Number, JsonSerializer.SerializeToElement(amount)),
        });

        configure?.Invoke(doc);

        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    private static FileOrigin NewFileOrigin(Guid documentId) => new(
        blobName: $"blobs/{documentId:N}.pdf",
        uploadedByUserName: "test-user",
        contentType: "application/pdf",
        contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        fileSize: 1024,
        originalFileName: "invoice.pdf");

    private static Guid TypeId => DeterministicGuid("type:" + TypeCode);
    private static Guid AmountFieldId => DeterministicGuid("field:amount");
    private static Guid DeterministicGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
