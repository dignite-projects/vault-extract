using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
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
    }
}

public class DocumentSegmentationJob_Tests : DocumentAITestBase<DocumentSegmentationJobTestModule>
{
    private readonly DocumentSegmentationJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
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
    public async Task Byte_Identical_Document_Slices_Are_Deduped_While_Distinct_Slices_Each_Spawn()
    {
        // #346 fix: a byte-identical slice collapses to one sub-document (an accidental repeat — mirrors the figure
        // path's identical-image de-dup) and is LOGGED, not silently dropped; the distinct slices still each spawn,
        // and SegmentKey stays a pure content hash (== FileOrigin.ContentHash == OriginConstituentKey).
        var containerId = await ArrangeContainerAsync("DOCA body line\nDOCA body line\nDOCB other line");
        StubSplit(("DOCA body", true), ("DOCA body", true), ("DOCB other", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2); // the duplicate "DOCA" slice was de-duplicated
            segments.Select(s => s.SliceText).OrderBy(t => t).ToArray()
                .ShouldBe(new[] { "DOCA body line", "DOCB other line" });
            // SegmentKey is the pure content hash of the slice text (no positional salt).
            foreach (var s in segments)
            {
                s.SegmentKey.ShouldBe(ContentHasher.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(s.SliceText)));
            }

            var derived = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            derived.Count.ShouldBe(2);
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

    private async Task<Guid> ArrangeContainerAsync(string markdown)
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

            await _documentRepository.InsertAsync(doc, autoSave: true);
        });

        return containerId;
    }

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
