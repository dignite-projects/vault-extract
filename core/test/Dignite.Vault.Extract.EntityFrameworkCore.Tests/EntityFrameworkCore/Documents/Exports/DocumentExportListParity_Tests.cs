using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.Exports;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.Exports;

/// <summary>
/// <c>DocumentAppService</c> resolves a blob container and a background-job manager it never reaches on the read
/// path; the export service needs neither. Substituting both is what lets one test class drive the list and the
/// export against the same SQLite database, which is the point.
/// </summary>
[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentExportListParityTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #501 item 1 + item 5: "the file is the view" is a claim about the <b>server</b>, and this is where it is
/// held. <c>DocumentAppService</c> and <c>DocumentExportAppService</c> used to hand-write the same metadata
/// predicates independently; #500's round-trip guard lives in TypeScript and compares filter <b>key names</b>,
/// so it stays green while the two predicate chains mean different things. These tests compare what the two
/// services actually <b>select</b> and in what <b>order</b>, which is the property the operator experiences.
/// <para>
/// Re-inline a predicate in either service and change it — <c>CabinetId</c> to include child cabinets is the
/// example #501 gives — and the parity test below reddens. That is the whole job: the shared
/// <c>DocumentQueries.ApplyMetadataFilter</c> chain makes the drift impossible, and this pins the behaviour so
/// a future refactor cannot quietly undo it.
/// </para>
/// </summary>
public class DocumentExportListParity_Tests : VaultExtractTestBase<DocumentExportListParityTestModule>
{
    private const string TypeCode = "invoice.general";

    private static readonly Guid TypeId = new("00000000-0000-0000-0000-0000000000ff");
    private static readonly Guid CabinetId = new("00000000-0000-0000-0000-0000000000c1");

    // Ids chosen so that descending-Id order (C, B, A) differs from the insertion order (B, A, C). Without a
    // tiebreaker the list returns whatever the database volunteers — in practice insertion order — while the
    // export has always ordered by Id. On a CreationTime tie the two therefore disagree.
    private static readonly Guid IdA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid IdC = new("33333333-3333-3333-3333-333333333333");

    private static readonly DateTime SharedCreationTime = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentExportAppService _exportAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICabinetRepository _cabinetRepository;

    public DocumentExportListParity_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _exportAppService = GetRequiredService<IDocumentExportAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
    }

    [Fact]
    public async Task List_and_export_agree_on_row_order_when_creation_times_tie()
    {
        // #501 item 5. CreationTime ties are ordinary for a batch upload; ApplySorting had no tiebreaker in any
        // branch, so the screen's order was database-determined while the file's was Id descending.
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync();
            await SeedAsync(IdB, "Doc B");
            await SeedAsync(IdA, "Doc A");
            await SeedAsync(IdC, "Doc C");
        });

        await WithUnitOfWorkAsync(async () =>
        {
            // Guard the premise: if ABP's audit interceptor had overwritten the seeded CreationTime, every row
            // would carry a distinct Clock.Now and this test would prove nothing about ties.
            var seeded = await _documentRepository.GetListAsync();
            seeded.Select(d => d.CreationTime).Distinct().Count()
                .ShouldBe(1, "the three documents must share one CreationTime for this to exercise the tiebreaker");
        });

        var listTitles = await ListTitlesAsync(new GetDocumentListInput { DocumentTypeCode = TypeCode });
        var exportTitles = await ExportTitlesAsync(new ExportDocumentsInput { DocumentTypeCode = TypeCode });

        listTitles.Count.ShouldBe(3);
        exportTitles.ShouldBe(listTitles);
    }

    [Fact]
    public async Task List_and_export_agree_on_row_order_when_creation_times_tie_under_ascending_sort()
    {
        // The ascending branch of ApplySorting needs the mirrored tiebreaker, not just the default branch.
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync();
            await SeedAsync(IdB, "Doc B");
            await SeedAsync(IdA, "Doc A");
            await SeedAsync(IdC, "Doc C");
        });

        var ascending = await ListTitlesAsync(
            new GetDocumentListInput { DocumentTypeCode = TypeCode, Sorting = "creationTime asc" });
        var descending = await ListTitlesAsync(
            new GetDocumentListInput { DocumentTypeCode = TypeCode, Sorting = "creationTime desc" });

        // A total order both ways: reversing the sort reverses the rows exactly.
        ascending.ShouldBe(descending.AsEnumerable().Reverse().ToList());
    }

    [Theory]
    [InlineData(nameof(DocumentLifecycleStatus))]
    [InlineData(nameof(CabinetId))]
    [InlineData("OriginDocumentId")]
    [InlineData("HasReviewReasons")]
    public async Task List_and_export_select_the_same_documents_for_a_shared_filter(string filter)
    {
        var containerId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync();
            await _cabinetRepository.InsertAsync(new Cabinet(CabinetId, null, "Filed"), autoSave: true);

            // "Plain" matches no filter below; each of the others matches exactly one.
            await SeedAsync(Guid.NewGuid(), "Plain");
            await SeedAsync(Guid.NewGuid(), "Failed", configure: d =>
                DocumentTestData.SetLifecycleStatus(d, DocumentLifecycleStatus.Failed));
            await SeedAsync(Guid.NewGuid(), "Filed", configure: d => d.SetCabinet(CabinetId));
            await SeedAsync(Guid.NewGuid(), "Queued", configure: d =>
                d.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true));

            await SeedAsync(containerId, "Container");
            await SeedDerivedAsync(Guid.NewGuid(), containerId, "Child");
        });

        var (listInput, exportInput) = filter switch
        {
            nameof(DocumentLifecycleStatus) => (
                new GetDocumentListInput { LifecycleStatus = DocumentLifecycleStatus.Failed },
                new ExportDocumentsInput { LifecycleStatus = DocumentLifecycleStatus.Failed }),
            nameof(CabinetId) => (
                new GetDocumentListInput { CabinetId = CabinetId },
                new ExportDocumentsInput { CabinetId = CabinetId }),
            "OriginDocumentId" => (
                new GetDocumentListInput { OriginDocumentId = containerId },
                new ExportDocumentsInput { OriginDocumentId = containerId }),
            _ => (
                new GetDocumentListInput { HasReviewReasons = true },
                new ExportDocumentsInput { HasReviewReasons = true }),
        };

        listInput.DocumentTypeCode = TypeCode;
        exportInput.DocumentTypeCode = TypeCode;

        var listTitles = await ListTitlesAsync(listInput);
        var exportTitles = await ExportTitlesAsync(exportInput);

        // Non-empty, so a filter that accidentally matches nothing cannot pass by vacuous agreement.
        listTitles.ShouldNotBeEmpty();
        exportTitles.ShouldBe(listTitles);
    }

    [Fact]
    public async Task Export_never_reaches_a_soft_deleted_document()
    {
        // Deliberately NOT parity: the list can open DataFilter.Disable<ISoftDelete>() for the recycle bin, the
        // export never does. #501 verified this as fail-closed and it must stay that way — a deleted document
        // may not leave through a file.
        var deletedId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync();
            await SeedAsync(deletedId, "Deleted");
            await SeedAsync(Guid.NewGuid(), "Live");
        });

        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(deletedId, autoSave: true));

        var exportTitles = await ExportTitlesAsync(new ExportDocumentsInput { DocumentTypeCode = TypeCode });

        exportTitles.ShouldBe(new[] { "Live" });
    }

    private async Task<List<string?>> ListTitlesAsync(GetDocumentListInput input)
    {
        PagedResultDto<DocumentListItemDto> page = null!;
        await WithUnitOfWorkAsync(async () => page = await _documentAppService.GetListAsync(input));
        return page.Items.Select(i => i.Title).ToList();
    }

    private async Task<List<string?>> ExportTitlesAsync(ExportDocumentsInput input)
    {
        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _exportAppService.ExportAsync(input);
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });

        // Column 3 is the fixed Title system column. Seeded titles contain no comma, so no unquoting is needed.
        return csv
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => (string?)line.TrimEnd('\r').Split(',')[3])
            .ToList();
    }

    private Task SeedTypeAsync() =>
        _documentTypeRepository.InsertAsync(new DocumentType(TypeId, null, TypeCode, "Invoice"), autoSave: true);

    private Task SeedAsync(Guid id, string title, Action<Document>? configure = null) =>
        PersistAsync(new Document(id, tenantId: null, DocumentTestData.NewFileOrigin(id)), title, configure);

    private Task SeedDerivedAsync(Guid id, Guid originDocumentId, string title) =>
        PersistAsync(
            Document.CreateDerived(id, tenantId: null, fileOrigin: null, originDocumentId, originConstituentKey: "seg-1"),
            title, configure: null);

    private Task PersistAsync(Document document, string title, Action<Document>? configure)
    {
        // Simulate a classified, titled document. CreationTime is pinned so every seeded row ties.
        DocumentTestData.MarkClassified(document, TypeId);
        DocumentTestData.SetTitle(document, title);
        DocumentTestData.SetCreationTime(document, SharedCreationTime);

        configure?.Invoke(document);

        return _documentRepository.InsertAsync(document, autoSave: true);
    }
}
