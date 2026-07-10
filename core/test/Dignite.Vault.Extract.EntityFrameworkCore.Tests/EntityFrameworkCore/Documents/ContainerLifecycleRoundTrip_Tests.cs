using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class ContainerLifecycleRoundTripTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // Same split seam as DocumentSegmentationJob_Tests: a partial substitute whose RunAsync each test stubs to a
        // chosen boundary set — no real LLM call. The real DocumentSegmentationJob, the real reclassify app service,
        // and the real ContainerMarker{Set,Cleared}EventHandler all run against the real SQLite DB.
        var workflow = Substitute.ForPartsOf<DocumentSegmentationWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new VaultExtractBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// Cross-entity invariant coverage for the FULL container lifecycle round trip (decision record #390): a single
/// document driven container → concrete → container through the <b>real</b> segmentation job + reclassify app service +
/// marker event handlers, asserting that the two coupled state machines (the <see cref="DocumentSegment"/> ledger's
/// <c>Status</c>/<c>Kind</c> and the <see cref="Document"/>'s <c>IsContainer</c>/<c>IsSegmented</c>) stay consistent
/// across the whole loop.
/// <para>
/// The existing per-leg tests each cover one transition in isolation (<c>IsSegmentedTransitionMatrix_Tests</c> is
/// domain-only; <c>ContainerReclassifyRetraction_Tests</c> / <c>Concrete_Doc_..._Re_Recognized_As_Container</c>
/// <b>pre-seed</b> a fabricated surviving-figure state). These tests instead verify the <b>composition seam</b>: that
/// the state a real retraction <i>produces</i> is a valid input to a subsequent real re-split.
/// </para>
/// </summary>
public class ContainerLifecycleRoundTrip_Tests
    : VaultExtractTestBase<ContainerLifecycleRoundTripTestModule>
{
    private readonly DocumentSegmentationJob _job;
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IRepository<DocumentType, Guid> _documentTypeRepository;
    private readonly DocumentSegmentationWorkflow _workflow;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    // A born-digital container Markdown carrying two text constituents (#301/#381 round-trip fixture). Kept plain
    // text on purpose: this suite exercises the container→type→container lifecycle, and figure-span routing has its
    // own coverage in DocumentSegmentationJob_Tests (Embedded_Figure_Span_In_A_Concrete_Document_Spawns_A_Figure_Sub_Document).
    private const string ContainerMarkdown = "Service A-B\nLease X-Y";

    public ContainerLifecycleRoundTrip_Tests()
    {
        _job = GetRequiredService<DocumentSegmentationJob>();
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
        _documentTypeRepository = GetRequiredService<IRepository<DocumentType, Guid>>();
        _workflow = GetRequiredService<DocumentSegmentationWorkflow>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    [Fact]
    public async Task Real_Split_Then_Real_Reclassify_Retracts_All_Text_Children_And_Clears_IsSegmented()
    {
        // Leg 1 — the REAL segmentation job produces the post-split state (2 Text children), instead of the
        // hand-seeded state the existing retraction test uses.
        var containerId = await ArrangeContainerAsync();
        StubSplit(("Service A-B", true), ("Lease X-Y", true));
        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2);
            segments.ShouldAllBe(s => s.Kind == DocumentSegmentKind.Text);
            (await _documentRepository.GetAsync(containerId)).IsSegmented.ShouldBeTrue();
        });

        // Leg 2 — the REAL reclassify app service drives container → concrete; the marker-cleared handler retracts
        // every Text child (a plain-text container has no Figure-kind constituent left to survive, contrast
        // ContainerReclassifyRetraction_Tests which pre-seeds a surviving Figure child by hand), and SetContainerFlag
        // clears IsSegmented.
        var typeId = await SeedDocumentTypeAsync("invoice.general");
        await _appService.ReclassifyAsync(containerId, new ReclassifyDocumentInput { DocumentTypeId = typeId });

        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(containerId);
            doc.IsContainer.ShouldBeFalse();
            doc.IsSegmented.ShouldBeFalse(); // cleared on the container→concrete transition (#378/#379)
            doc.DocumentTypeId.ShouldBe(typeId);

            // Both Text children are retracted (soft-deleted) and their segment rows removed outright.
            var survivors = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            survivors.ShouldBeEmpty();

            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Re_Recognized_Container_Re_Splits_And_Re_Spawns_Text_Children_Without_Colliding()
    {
        // The full loop's third leg + the #391 fix: after container→concrete, re-recognizing the document as a
        // container again must clear IsSegmented so the Phase-A split re-runs, AND the re-split must cleanly re-spawn
        // the text children. Retraction soft-deleted the original text children and Document.Markdown is immutable, so
        // the re-split reuses the same OriginConstituentKey values — before #391 that collided with the soft-deleted
        // rows on the then-existing (OriginDocumentId, OriginConstituentKey) unique index (its filter excluded
        // IsDeleted = 0, freeing the slot) and the job retried forever. #481 removed that index from Document
        // altogether — idempotency now lives on the DocumentSegment ledger's own (SourceDocumentId, SegmentKey)
        // unique index instead — so this collision class can no longer recur on Document at all; this test still
        // guards the re-spawn round trip.
        var containerId = await ArrangeContainerAsync();
        StubSplit(("Service A-B", true), ("Lease X-Y", true));
        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        var typeId = await SeedDocumentTypeAsync("invoice.general");
        await _appService.ReclassifyAsync(containerId, new ReclassifyDocumentInput { DocumentTypeId = typeId });

        // Re-recognize as a container (mirrors the classification job's container branch).
        await MarkAsContainerInUowAsync(containerId);

        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(containerId);
            doc.IsContainer.ShouldBeTrue();
            doc.IsSegmented.ShouldBeFalse(); // the re-recognition cleared the marker → a re-split is allowed
            doc.DocumentTypeId.ShouldBeNull();
        });

        // Phase A re-runs (the marker no longer short-circuits it): assert the LLM split is invoked on re-enqueue.
        _workflow.ClearReceivedCalls();
        StubSplit(("Service A-B", true), ("Lease X-Y", true));
        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await _workflow.Received().RunAsync(
            Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(containerId);
            doc.IsContainer.ShouldBeTrue();
            doc.IsSegmented.ShouldBeTrue();                                    // the re-split completed
            doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None);           // no SegmentationIncomplete collision flag

            // Both text constituents are re-split and re-spawned, all reaching Spawned — no unique-index collision
            // with the soft-deleted originals (#391).
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2);
            segments.ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);
            segments.ShouldAllBe(s => s.Kind == DocumentSegmentKind.Text);

            // Two LIVE derived children (freshly re-spawned); the two originally-retracted text children remain
            // only as soft-deleted archives, excluded by the ISoftDelete filter.
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
        });
    }

    private async Task<Guid> ArrangeContainerAsync()
    {
        var containerId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = new Document(
                containerId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{containerId:N}.pdf",
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                    fileSize: 2048,
                    originalFileName: "bundle.pdf"));

            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, [ContainerMarkdown]);
            typeof(Document)
                .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(doc, null);

            await _documentRepository.InsertAsync(doc, autoSave: true);
        });

        return containerId;
    }

    private async Task<Guid> SeedDocumentTypeAsync(string typeCode)
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, typeCode, typeCode), autoSave: true));
        return typeId;
    }

    private async Task MarkAsContainerInUowAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);
        var document = await _documentRepository.GetAsync(documentId);
        typeof(Document)
            .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(document, null);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        await uow.CompleteAsync();
    }

    private void StubSplit(params (string Marker, bool IsSubDocument)[] boundaries)
    {
        var outcome = new DocumentSegmentationOutcome();
        foreach (var (marker, isSubDocument) in boundaries)
        {
            outcome.Boundaries.Add(new SegmentBoundary(marker, isSubDocument));
        }

        _workflow.RunAsync(Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
    }
}
