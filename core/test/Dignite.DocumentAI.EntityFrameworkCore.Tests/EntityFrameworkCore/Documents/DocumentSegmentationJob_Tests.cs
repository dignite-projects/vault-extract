using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Figures;
using Dignite.DocumentAI.Documents.Pipelines.Segmentation;
using Dignite.DocumentAI.Documents.Pipelines.TextExtraction;
using Dignite.DocumentAI.Documents.Segments;
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
using Xunit;

namespace Dignite.DocumentAI.EntityFrameworkCore.Documents;

[DependsOn(typeof(DocumentAIEntityFrameworkCoreTestModule))]
public class DocumentSegmentationJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // Split seam: a partial substitute of the segmentation workflow whose RunAsync each test stubs to a chosen
        // boundary set — no real LLM call (mirrors the figure routing test's classification-workflow seam).
        var workflow = Substitute.ForPartsOf<DocumentSegmentationWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new DocumentAIBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);

        // Lower the caps so the bound tests don't need 50 slices / 200k chars. All other tests in this class use
        // <= 2 distinct slices and < 100-char Markdown, so they are unaffected.
        context.Services.Configure<DocumentAIBehaviorOptions>(o =>
        {
            o.MaxSegmentsPerDocument = 4;
            o.MaxSegmentationMarkdownLength = 100;
        });
    }
}

public class DocumentSegmentationJob_Tests : DocumentAITestBase<DocumentSegmentationJobTestModule>
{
    private readonly DocumentSegmentationJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IRepository<DocumentFigure, Guid> _figureRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly DocumentSegmentationWorkflow _workflow;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentSegmentationJob_Tests()
    {
        _job = GetRequiredService<DocumentSegmentationJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
        _figureRepository = GetRequiredService<IRepository<DocumentFigure, Guid>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _workflow = GetRequiredService<DocumentSegmentationWorkflow>();
        _blobContainer = GetRequiredService<IBlobContainer<DocumentAIDocumentContainer>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Container_Splits_Into_Seeded_Derived_Documents()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2);
            segments.ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);

            var derived = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            derived.Count.ShouldBe(2);
            // Each derived sub-document is keyed by its slice hash (= the segment key) and carries a Markdown FileOrigin.
            derived.ShouldAllBe(d => d.FileOrigin.ContentType == "text/markdown");
            foreach (var d in derived)
            {
                d.OriginConstituentKey.ShouldNotBeNull();
                segments.ShouldContain(s => s.SegmentKey == d.OriginConstituentKey && s.RoutedDocumentId == d.Id);
                d.FileOrigin.ContentHash.ShouldBe(d.OriginConstituentKey);
            }
        });

        await _eventBus.Received(2).PublishAsync(Arg.Any<DocumentUploadedEto>());
        // Each derived sub-document runs the full normal pipeline (text-extraction enqueued).
        await _backgroundJobManager.Received(2).EnqueueAsync(
            Arg.Any<DocumentTextExtractionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Inlined_Figure_Transcription_Is_Not_Spawned_As_A_Sub_Document()
    {
        // #359: figure routing (#306) is the single owner of figure-as-document. A born-digital container whose
        // Markdown carries an inlined image-invoice transcription (#301) between two real text documents must NOT
        // re-spawn that invoice as a text sub-document when the LLM (ignoring the segmentation prompt's instruction)
        // cuts the inlined transcription into its own slice — that would duplicate the figure-routed invoice (the
        // text-slice hash and the image-bytes hash differ, so the unique index cannot dedup them). The figure slice is
        // downgraded to NotADocument; only the two genuine text documents spawn.
        var invoiceTranscription = "INVOICE No 42 Total 100";
        var containerId = await ArrangeContainerAsync(
            $"Service Agreement A-B\n{invoiceTranscription}\nLease Contract X-Y",
            figureTranscriptions: new[] { invoiceTranscription });
        StubSplit(("Service Agreement", true), ("INVOICE No 42", true), ("Lease Contract", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var invoiceSliceKey = ContentHasher.Sha256Hex(
                System.Text.Encoding.UTF8.GetBytes(invoiceTranscription));

            // Only the two genuine text documents spawn; the inlined invoice figure is not re-spawned by segmentation.
            var derived = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            derived.Count.ShouldBe(2);
            derived.ShouldNotContain(d => d.OriginConstituentKey == invoiceSliceKey);

            // The figure slice is persisted as NotADocument (audit), the two real docs as Spawned.
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(3);
            segments.Count(s => s.Status == DocumentSegmentStatus.Spawned).ShouldBe(2);
            segments.ShouldContain(s =>
                s.SegmentKey == invoiceSliceKey && s.Status == DocumentSegmentStatus.NotADocument);
        });
    }

    [Fact]
    public async Task Document_Whose_Body_Merely_Contains_A_Figure_Still_Spawns()
    {
        // #359 guard against over-suppression: the suppression only fires when a slice is ESSENTIALLY a figure
        // transcription (>= FigureDominanceRatio of it). A real document whose body merely INCLUDES a small inlined
        // figure keeps a large non-figure remainder, so the transcription is a minority of the slice and the document
        // still spawns. If suppression keyed off `Contains` alone, this slice would be wrongly dropped, leaving fewer
        // than two document slices and flagging the container — so this test fails on a too-aggressive threshold.
        var containerId = await ArrangeContainerAsync(
            "Service Agreement A-B\nFig 1 logo\nLease Contract X-Y body text",
            figureTranscriptions: new[] { "Fig 1 logo" });
        StubSplit(("Service Agreement", true), ("Lease Contract", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            // Both real documents spawn; the embedded figure does not suppress its host document.
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId))
                .ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);
        });
    }

    [Fact]
    public async Task Untrusted_Split_Flags_Container_And_Spawns_Nothing()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        // Markers the LLM "returned" but that do not appear verbatim -> MarkdownSlicer rejects the split.
        StubSplit(("Phantom marker one", true), ("Phantom marker two", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var container = await _documentRepository.GetAsync(containerId);
            container.ReviewReasons.ShouldBe(DocumentReviewReasons.SegmentationIncomplete);

            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentUploadedEto>());
    }

    [Fact]
    public async Task Fewer_Than_Two_Document_Slices_Flags_Container()
    {
        var containerId = await ArrangeContainerAsync("Invoice A only");
        StubSplit(("Invoice A", true)); // a single document slice is not a real bundle

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Resumes_From_Existing_Segments_Without_Re_Splitting()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");

        // Simulate a crash after the split persisted but before any spawn: two Pending segments already exist.
        await WithUnitOfWorkAsync(async () =>
        {
            await _segmentRepository.InsertAsync(NewSegment(containerId, "Invoice A first", 0), autoSave: true);
            await _segmentRepository.InsertAsync(NewSegment(containerId, "Invoice B second", 1), autoSave: true);
        });

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        // The LLM split must be skipped on resume (segments already exist), and the Pending segments spawn.
        await _workflow.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId))
                .ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);
        });
    }

    [Fact]
    public async Task Rerun_Is_Idempotent_And_Does_Not_Duplicate_Sub_Documents()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });
        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2));
    }

    [Fact]
    public async Task Byte_Identical_Slices_Flag_For_Review_Instead_Of_Silently_Dropping()
    {
        // #346 fix (Codex review): content alone can't tell an accidental duplicate from a genuine repeated
        // instance, and the pure content hash is the idempotency identity (no positional salt). So a byte-identical
        // slice flags the WHOLE container for human review rather than silently collapsing and risking dropping a
        // real document. Nothing is persisted/spawned until a human resolves it.
        var containerId = await ArrangeContainerAsync("DOCA body line\nDOCA body line\nDOCB other line");
        StubSplit(("DOCA body", true), ("DOCA body", true), ("DOCB other", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Reclassified_Container_Is_Skipped_Without_Splitting_Or_Spawning()
    {
        // #346 fix (Codex review): a job enqueued for a mis-detected container that was since reclassified to a
        // concrete type (IsContainer cleared) must NOT split/spawn — that would inject spurious sub-documents
        // downstream alongside the now-typed document's own fields.
        var docId = await ArrangeContainerAsync("Invoice A first\nInvoice B second", asContainer: false);
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = docId });

        await _workflow.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await WithUnitOfWorkAsync(async () =>
        {
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == docId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == docId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Retry_That_Finds_All_Segments_Already_Spawned_Clears_A_Stale_Flag()
    {
        // #346 fix (sweep): a prior/concurrent run flagged the container, and a concurrent worker spawned the last
        // slice, so THIS run finds zero Pending. It must still clear the stale flag (FinalizeSegmentationFlagAsync
        // runs even with nothing Pending), so a fully-segmented container does not linger in the review queue.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        await WithUnitOfWorkAsync(async () =>
        {
            var container = await _documentRepository.GetAsync(containerId);
            container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: true);
            await _documentRepository.UpdateAsync(container, autoSave: true);

            // Two segments already fully Spawned (as if a concurrent worker finished them).
            var s1 = NewSegment(containerId, "Invoice A first", 0);
            s1.MarkSpawned(_guidGenerator.Create());
            await _segmentRepository.InsertAsync(s1, autoSave: true);
            var s2 = NewSegment(containerId, "Invoice B second", 1);
            s2.MarkSpawned(_guidGenerator.Create());
            await _segmentRepository.InsertAsync(s2, autoSave: true);
        });

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        // No LLM re-split (segments already exist), and the stale flag is cleared.
        await _workflow.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(containerId)).ReviewReasons.ShouldBe(DocumentReviewReasons.None));
    }

    [Fact]
    public async Task Reclassify_During_Phase_A_Does_Not_Re_Flag_The_Now_Typed_Document()
    {
        // #346 fix (high-effort review): the container passes LoadAsync (still a container), but an operator
        // reclassifies it to a concrete type DURING the slow LLM split (IsContainer cleared). The split then fails
        // verification, so the Phase A flag path (MarkSegmentationIncompleteAsync) runs — it must re-check IsContainer
        // and NOT re-flag the now-typed document, or it would push the operator's reclassification back into the
        // review queue with no way to clear it (no segment rows are persisted on this path, so the count-driven clear
        // in FinalizeSegmentationFlagAsync never runs). Mirrors the guard CommitSpawnAsync / FinalizeSegmentationFlagAsync apply.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");

        // Untrusted markers -> the split is rejected -> MarkSegmentationIncompleteAsync runs.
        StubSplit(("Phantom marker one", true), ("Phantom marker two", true));
        // ...but mid-split (the external phase), the document is reclassified away from container — the race this guard closes.
        _workflow
            .When(x => x.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => ReclassifyAwayFromContainer(containerId));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(containerId);
            doc.IsContainer.ShouldBeFalse();
            // The now-typed document must NOT carry the segmentation-incomplete flag.
            doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Unparseable_LLM_Response_Flags_Container_Without_Faulting_The_Job()
    {
        // #346 fix: a schema-drift / non-JSON structured response is caught and flagged for review, not allowed to
        // fault the job into an endless ABP retry loop.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        _workflow.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<DocumentSegmentationOutcome>(_ => throw new JsonException("schema drift"));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Spawn_Failure_Flags_Container_And_Leaves_Segments_Pending()
    {
        // #346 fix: Phase B failures are not silent either — the container is flagged before the rethrow so an
        // operator sees it even after ABP exhausts retries.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));
        _blobContainer
            .When(x => x.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("blob store down"));

        await Should.ThrowAsync<AggregateException>(
            () => _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId }));

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId))
                .ShouldAllBe(s => s.Status == DocumentSegmentStatus.Pending);
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Full_Segmentation_Clears_A_Prior_SegmentationIncomplete_Flag()
    {
        // #346 fix: a retry that finally spawns every slice clears the flag a prior partial run left, so a container
        // that ultimately segments fully does not linger in the review queue.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        await WithUnitOfWorkAsync(async () =>
        {
            var container = await _documentRepository.GetAsync(containerId);
            container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: true);
            await _documentRepository.UpdateAsync(container, autoSave: true);
        });
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons.ShouldBe(DocumentReviewReasons.None);
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
        });
    }

    [Fact]
    public async Task Total_Slice_Count_Over_The_Cap_Flags_Container_Even_When_Most_Are_Non_Document()
    {
        // #346 fix: the cap bounds TOTAL slices, not just document slices — a flood of cover/index slices cannot
        // insert unbounded rows. 2 documents + 3 covers = 5 total > the test cap of 4, so the container is flagged
        // even though only 2 are documents.
        var containerId = await ArrangeContainerAsync("A doc\nB doc\nC cover\nD cover\nE cover");
        StubSplit(("A doc", true), ("B doc", true), ("C cover", false), ("D cover", false), ("E cover", false));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Oversized_Container_Markdown_Flags_For_Review_Without_Calling_The_LLM()
    {
        // #346 fix: segmentation feeds the whole Markdown to the LLM, so an over-limit container degrades to a
        // review signal instead of paying for an enormous prompt — and the LLM is never called.
        var oversized = new string('x', 150); // > the test cap of 100
        var containerId = await ArrangeContainerAsync(oversized);

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await _workflow.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete));
    }

    [Fact]
    public async Task Two_Splits_Of_The_Same_Container_Collide_On_The_Ordinal_Unique_Index()
    {
        // #346 fix: the unique (SourceDocumentId, Ordinal) index makes concurrent double-splits mutually exclusive —
        // every split numbers its first slice Ordinal 0, so the second committer is rejected by the DB.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        await WithUnitOfWorkAsync(async () =>
            await _segmentRepository.InsertAsync(NewSegment(containerId, "first split slice", 0), autoSave: true));

        await Should.ThrowAsync<Exception>(() => WithUnitOfWorkAsync(async () =>
            await _segmentRepository.InsertAsync(NewSegment(containerId, "second split slice", 0), autoSave: true)));
    }

    private async Task<Guid> ArrangeContainerAsync(
        string markdown, bool asContainer = true, string[]? figureTranscriptions = null)
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
                .Invoke(doc, [markdown]);

            // The segmentation job only runs for actual containers; mark it unless the test wants a doc that was
            // reclassified away from container (IsContainer == false).
            if (asContainer)
            {
                typeof(Document)
                    .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(doc, null);
            }

            await _documentRepository.InsertAsync(doc, autoSave: true);

            // #359: seed DocumentFigure rows as text extraction (#306) would have, carrying the verbatim transcription
            // that PdfExtractor inlined into the container Markdown (#301). Segmentation reads these to refuse carving
            // an already-inlined figure into its own sub-document.
            foreach (var transcription in figureTranscriptions ?? Array.Empty<string>())
            {
                var contentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
                await _figureRepository.InsertAsync(new DocumentFigure(
                    _guidGenerator.Create(), tenantId: null, sourceDocumentId: containerId,
                    contentHash: contentHash, cropBlobName: $"figures/{containerId}/{contentHash}",
                    contentType: "image/png", transcription: transcription, pageNumber: 1), autoSave: true);
            }
        });

        return containerId;
    }

    // Simulates an operator reclassifying the container to a concrete type WHILE the LLM split runs (the external
    // phase). Mirrors how ArrangeContainerAsync sets Markdown — flips IsContainer via its private setter and commits,
    // so the job's subsequent FindActiveContainerAsync sees the document is no longer a container. Blocking is safe:
    // xUnit async tests run with no SynchronizationContext, and at this point the job holds no ambient UoW.
    private void ReclassifyAwayFromContainer(Guid containerId)
        => WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(containerId);
            typeof(Document)
                .GetProperty(nameof(Document.IsContainer))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, [false]);
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        }).GetAwaiter().GetResult();

    private DocumentSegment NewSegment(Guid containerId, string sliceText, int ordinal)
        => new(
            _guidGenerator.Create(),
            tenantId: null,
            sourceDocumentId: containerId,
            segmentKey: ContentHasher.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(sliceText)),
            sliceText: sliceText,
            ordinal: ordinal);

    private void StubSplit(params (string Marker, bool IsDocument)[] boundaries)
    {
        var outcome = new DocumentSegmentationOutcome();
        foreach (var (marker, isDocument) in boundaries)
        {
            outcome.Boundaries.Add(new SegmentBoundary(marker, isDocument));
        }

        _workflow.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(outcome);
    }
}
