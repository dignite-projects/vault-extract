using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentSegmentationJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // Split seam: a partial substitute of the segmentation workflow whose RunAsync each test stubs to a chosen
        // boundary set — no real LLM call (mirrors the figure routing test's classification-workflow seam).
        var workflow = Substitute.ForPartsOf<DocumentSegmentationWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new VaultExtractBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);

        // Lower the caps so the bound tests don't need 50 slices / 200k chars. All other tests in this class use
        // <= 2 distinct slices and < 130-char Markdown (the *[Image OCR p:N]* / *[End OCR]* figure markers add ~30 chars per figure),
        // so they are unaffected.
        context.Services.Configure<VaultExtractBehaviorOptions>(o =>
        {
            o.MaxSegmentsPerDocument = 4;
            o.MaxSegmentationMarkdownLength = 130;
        });
    }
}

public class DocumentSegmentationJob_Tests : VaultExtractTestBase<DocumentSegmentationJobTestModule>
{
    private readonly DocumentSegmentationJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly DocumentSegmentationWorkflow _workflow;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentSegmentationJob_Tests()
    {
        _job = GetRequiredService<DocumentSegmentationJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _workflow = GetRequiredService<DocumentSegmentationWorkflow>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Container_Splits_Into_Seeded_Derived_Documents()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2);
            segments.ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);
            // Born-digital text constituents -> Kind.Text (drives the #364 retraction filter).
            segments.ShouldAllBe(s => s.Kind == DocumentSegmentKind.Text);

            var parent = await _documentRepository.GetAsync(containerId);
            var derived = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            derived.Count.ShouldBe(2);
            // #481: each derived sub-document SHARES the parent's upload blob (never a copy) -- its Markdown is
            // still seeded from the segment's SliceText, never re-extracted from that blob -- and its identity is
            // still the OriginConstituentKey (= the segment key / slice hash).
            derived.ShouldAllBe(d => d.FileOrigin.BlobName == parent.FileOrigin.BlobName);
            derived.ShouldAllBe(d => d.FileOrigin.ContentHash == parent.FileOrigin.ContentHash);
            foreach (var d in derived)
            {
                d.OriginConstituentKey.ShouldNotBeNull();
                segments.ShouldContain(s => s.SegmentKey == d.OriginConstituentKey && s.RoutedDocumentId == d.Id);
            }
        });

        await _eventBus.Received(2).PublishAsync(Arg.Any<DocumentUploadedEto>());
        // Each derived sub-document runs the full normal pipeline (text-extraction enqueued).
        await _backgroundJobManager.Received(2).EnqueueAsync(
            Arg.Any<DocumentParseJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Spawned_Derived_Document_Reports_Zero_Size_Upload_Event()
    {
        // #485 (A2): a derived document only ever SHARES its source's blob (#481) -- it contributes no
        // independent storage of its own -- so DocumentUploadedEto must report FileSize 0 / FileName null /
        // ContentType null, even though the spawned Document's own FileOrigin is still the parent's whole
        // (non-null, non-zero) snapshot. Otherwise a downstream consumer accumulating storage/quota over
        // DocumentUploadedEto.FileSize would N×-count the same bytes once per sub-document, contradicting
        // IDocumentRepository.GetStatisticsAsync's own exclusion of derived rows from the byte sum.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await _eventBus.Received(2).PublishAsync(Arg.Is<DocumentUploadedEto>(
            e => e.FileSize == 0 && e.FileName == null && e.ContentType == null));

        await WithUnitOfWorkAsync(async () =>
        {
            var parent = await _documentRepository.GetAsync(containerId);
            var derived = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            // The derived Document's own FileOrigin (persisted state, distinct from the upload EVENT above) is
            // still the parent's whole shared snapshot, non-zero.
            derived.ShouldAllBe(d => d.FileOrigin.FileSize == parent.FileOrigin.FileSize && d.FileOrigin.FileSize > 0);
        });
    }

    [Fact]
    public async Task Embedded_Figure_Span_In_A_Concrete_Document_Spawns_Nothing()
    {
        // #487 Phase A: the figure image storage/retention chain (#477/#478) was removed — a figure span is never
        // routed anywhere anymore, unconditionally. A single concrete-typed document (NOT a container) whose
        // Document.Markdown carries an inlined, marker-bracketed image-invoice transcription is SKIPPED at
        // detection: no segment row is persisted at all, and nothing spawns. The transcription stays inline in the
        // parent's Markdown; the parent keeps its own type and is never flagged (it extracts normally — a figure
        // never routing is not the parent's problem).
        var invoiceText = "INVOICE No 42 Total 100";
        var marked = $"Contract body\n{ImageOcrMarkup.Wrap(invoiceText, 1)}";
        var docId = await ArrangeContainerAsync(
            markdown: "Contract body", asContainer: false, markedMarkdown: marked);

        // The figure span's first line IS the open sentinel — that is the verbatim marker the LLM returns for it.
        StubSplit(("Contract body", false), (ImageOcrMarkup.OpenPagePrefix + "1]*", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = docId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == docId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == docId)).ShouldBeEmpty();

            // The parent keeps its own type and is not flagged for review (embedded-document mode never flags).
            var parent = await _documentRepository.GetAsync(docId);
            parent.IsContainer.ShouldBeFalse();
            parent.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentUploadedEto>());
    }

    [Fact]
    public async Task Text_Constituent_Embedding_An_Inline_Figure_Is_Kind_Text_Not_Figure()
    {
        // #371 hardening (own /code-review): a genuine text constituent (a contract) that embeds an inline figure —
        // its slice is prose PLUS an *[Image OCR]* block (#301) — must be recorded Kind.Text. The span's kind is
        // the kind of its OPENING boundary (prose), NOT "does the body contain a sentinel somewhere". Otherwise it
        // would be mislabeled Figure and survive the container→type retraction (which keeps Kind==Figure), a
        // #364-class stale-sub-document leak. The child's seed also strips the sentinels (the inline figure is just
        // inline text in the spawned child).
        var marked = $"Contract A clauses\n{ImageOcrMarkup.Wrap("FIG seal stamp", 1)}\nmore clauses\nContract B clauses";
        var containerId = await ArrangeContainerAsync(
            markdown: "Contract A clauses\nmore clauses\nContract B clauses", asContainer: true, markedMarkdown: marked);

        // The LLM returns each contract as ONE span keyed on its prose first line; the inline figure stays inside
        // contract A's span rather than being emitted as its own *[Image OCR]* boundary.
        StubSplit(
            ("Contract A clauses", true),
            ("Contract B clauses", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2);
            // Both opened on prose -> both Kind.Text, even though contract A's slice body carries an *[Image OCR]*
            // block. NONE mislabeled Figure.
            segments.ShouldAllBe(s => s.Kind == DocumentSegmentKind.Text);

            var contractASeg = segments.Single(s => s.SliceText.Contains("Contract A clauses"));
            contractASeg.SliceText.ShouldContain("FIG seal stamp");
            contractASeg.SliceText.ShouldNotContain(ImageOcrMarkup.OpenPagePrefix); // sentinels stripped from the child seed
            contractASeg.SliceText.ShouldNotContain(ImageOcrMarkup.CloseMarker);
        });
    }

    [Fact]
    public async Task Concrete_Doc_With_A_Routed_Figure_Re_Recognized_As_Container_Still_Runs_The_Split()
    {
        // #372 (own /code-review): since #371 a concrete document with an embedded figure routes a Kind=Figure segment
        // (embedded mode). If that document is later re-recognized as a container (#355), the container split must STILL
        // run and add the bundle's Kind=Text constituents — the leftover Figure row must NOT be mistaken for a completed
        // split (the pre-#372 "any row exists -> skip" guard would leave the container forever undecomposed), and the
        // new rows must not collide with it on the unique (SourceDocumentId, Ordinal)/(SourceDocumentId, SegmentKey)
        // indexes. The container split re-detects that same figure, and it is inserted idempotently (skipped, never
        // duplicated).
        var figureText = "INVOICE No 9 Total 9";
        var marked = $"Invoice A first\n{ImageOcrMarkup.Wrap(figureText, 1)}\nInvoice B second";
        var containerId = await ArrangeContainerAsync(
            markdown: "Invoice A first\nInvoice B second", asContainer: true, markedMarkdown: marked);

        var figureKey = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(figureText));
        // Pre-seed the figure already routed by the prior concrete-embedded run: Kind=Figure, Ordinal 0, Spawned.
        await WithUnitOfWorkAsync(async () =>
        {
            var fig = new DocumentSegment(
                _guidGenerator.Create(), tenantId: null, sourceDocumentId: containerId,
                segmentKey: figureKey, sliceText: figureText, ordinal: 0, kind: DocumentSegmentKind.Figure);
            fig.MarkSpawned(_guidGenerator.Create());
            await _segmentRepository.InsertAsync(fig, autoSave: true);
        });

        // The container split re-detects all three spans, INCLUDING the already-routed figure.
        StubSplit(
            ("Invoice A first", true),
            (ImageOcrMarkup.OpenPagePrefix + "1]*", true),
            ("Invoice B second", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        // Phase A ran: the leftover figure row did NOT short-circuit the container split (contrast the resume tests).
        await _workflow.Received().RunAsync(
            Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            // The pre-existing figure (kept) + the two new text constituents — no duplicate figure, no ordinal collision.
            segments.Count.ShouldBe(3);
            segments.Count(s => s.SegmentKey == figureKey).ShouldBe(1);
            segments.Single(s => s.SegmentKey == figureKey).Kind.ShouldBe(DocumentSegmentKind.Figure);
            segments.Count(s => s.Kind == DocumentSegmentKind.Text).ShouldBe(2);
            segments.Select(s => s.Ordinal).Distinct().Count().ShouldBe(3);

            // The two text constituents spawned; the figure was already Spawned and is not re-spawned.
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
        });
    }

    [Fact]
    public async Task Container_Re_Split_Omitting_The_Surviving_Figure_Still_Persists_The_New_Text_Constituent()
    {
        // #377 edge 1: a concrete doc with one already-routed figure is re-recognized as a container; the
        // container-mode re-split returns exactly ONE new text constituent and (non-deterministically) OMITS the
        // figure's *[Image OCR]* boundary this run. The "≥2 real bundle" guard must count the surviving figure row
        // toward the bundle (not just this run's spans), so the legitimate text constituent is PERSISTED rather than
        // dropped and the container flagged incomplete. Pre-#377 the guard saw prepared.Count == 1 < 2 and flagged.
        var figureText = "INVOICE No 9 Total 9";
        var marked = $"Invoice A whole body\n{ImageOcrMarkup.Wrap(figureText, 1)}";
        var containerId = await ArrangeContainerAsync(
            markdown: "Invoice A whole body", asContainer: true, markedMarkdown: marked);

        var figureKey = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(figureText));
        // The figure was routed by the embedded run; #355 then re-recognized the doc as a container -> marker cleared.
        await WithUnitOfWorkAsync(async () =>
        {
            var fig = new DocumentSegment(
                _guidGenerator.Create(), tenantId: null, sourceDocumentId: containerId,
                segmentKey: figureKey, sliceText: figureText, ordinal: 0, kind: DocumentSegmentKind.Figure);
            fig.MarkSpawned(_guidGenerator.Create());
            await _segmentRepository.InsertAsync(fig, autoSave: true);
        });

        // The container re-split returns ONLY the one text constituent (it omits the figure boundary this run).
        StubSplit(("Invoice A whole body", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            // Surviving figure + the newly persisted text = a real 2-constituent bundle; NOT flagged incomplete.
            segments.Count.ShouldBe(2);
            segments.Count(s => s.Kind == DocumentSegmentKind.Figure).ShouldBe(1);
            segments.Count(s => s.Kind == DocumentSegmentKind.Text).ShouldBe(1);

            var container = await _documentRepository.GetAsync(containerId);
            container.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
            container.IsSegmented.ShouldBeTrue();
        });
    }

    [Fact]
    public async Task Untrusted_Split_Flags_Container_And_Spawns_Nothing()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        // Markers the LLM "returned" but that do not appear verbatim -> MarkdownSlicer rejects the split.
        StubSplit(("Phantom marker one", true), ("Phantom marker two", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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

        // Simulate a crash after the split persisted but before any spawn. The rows and the IsSegmented marker commit
        // atomically (#377), so a realistic crashed-after-split state has both: two Pending segments + the marker.
        await WithUnitOfWorkAsync(async () =>
        {
            await _segmentRepository.InsertAsync(NewSegment(containerId, "Invoice A first", 0), autoSave: true);
            await _segmentRepository.InsertAsync(NewSegment(containerId, "Invoice B second", 1), autoSave: true);
            var doc = await _documentRepository.GetAsync(containerId);
            doc.MarkSegmented();
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        // The LLM split must be skipped on resume (segments already exist), and the Pending segments spawn.
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>());
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

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });
        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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
        // concrete type (IsContainer cleared) must NOT split/spawn a text bundle — that would inject spurious
        // sub-documents downstream alongside the now-typed document's own fields. With no figure spans in the
        // Markdown the embedded-document mode finds nothing standalone to route, so it is a clean no-op.
        var docId = await ArrangeContainerAsync("Invoice A first\nInvoice B second", asContainer: false);
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = docId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == docId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == docId)).ShouldBeEmpty();
            // An embedded-document source is never flagged (it extracts normally).
            (await _documentRepository.GetAsync(docId)).ReviewReasons.ShouldBe(DocumentReviewReasons.None);
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentUploadedEto>());
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
            container.MarkSegmented(); // #377: a successful prior split set the marker atomically with the rows
            await _documentRepository.UpdateAsync(container, autoSave: true);

            // Two segments already fully Spawned (as if a concurrent worker finished them).
            var s1 = NewSegment(containerId, "Invoice A first", 0);
            s1.MarkSpawned(_guidGenerator.Create());
            await _segmentRepository.InsertAsync(s1, autoSave: true);
            var s2 = NewSegment(containerId, "Invoice B second", 1);
            s2.MarkSpawned(_guidGenerator.Create());
            await _segmentRepository.InsertAsync(s2, autoSave: true);
        });

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        // No LLM re-split (segments already exist), and the stale flag is cleared.
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>());
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
        // in FinalizeSegmentationFlagAsync never runs). Mirrors the guard SpawnDerivedDocumentAsync / FinalizeSegmentationFlagAsync apply.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");

        // Untrusted markers -> the split is rejected -> MarkSegmentationIncompleteAsync runs.
        StubSplit(("Phantom marker one", true), ("Phantom marker two", true));
        // ...but mid-split (the external phase), the document is reclassified away from container — the race this guard closes.
        _workflow
            .When(x => x.RunAsync(
                Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>()))
            .Do(_ => ReclassifyAwayFromContainer(containerId));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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
        _workflow.RunAsync(Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>())
            .Returns<DocumentSegmentationOutcome>(_ => throw new JsonException("schema drift"));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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
        // A sub-document has no source blob to write, so the canonical Phase B failure is now an egress step inside the
        // spawn UoW. Make DocumentUploadedEto publishing throw: it runs after the derived insert but before the
        // segment's Spawned mark is flushed, so the segment is left Pending (ABP then retries) and the job rethrows.
        // (The test UoW disables transactions, so the autosaved derived row is not rolled back here as the spawn UoW
        // would in production — this test asserts the retry contract: container flagged + segment Pending + rethrow.)
        _eventBus
            .PublishAsync(Arg.Any<DocumentUploadedEto>())
            .ThrowsAsync(new InvalidOperationException("event bus down"));

        await Should.ThrowAsync<AggregateException>(
            () => _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId }));

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId))
                .ShouldAllBe(s => s.Status == DocumentSegmentStatus.Pending);
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

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons.ShouldBe(DocumentReviewReasons.None);
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
        });
    }

    [Fact]
    public async Task Total_Slice_Count_Over_The_Cap_Flags_Container_Even_When_Most_Are_Non_Document()
    {
        // #346 fix: the cap bounds the spawnable slices — a flood of slices cannot insert unbounded rows. 5 document
        // slices > the test cap of 4, so the container is flagged.
        var containerId = await ArrangeContainerAsync("A doc\nB doc\nC doc\nD doc\nE doc");
        StubSplit(("A doc", true), ("B doc", true), ("C doc", true), ("D doc", true), ("E doc", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

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
        var oversized = new string('x', 150); // > the test cap of 130
        var containerId = await ArrangeContainerAsync(oversized);

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { SourceDocumentId = containerId });

        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<SubDocumentDetectionContext>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task Sequential_Double_Spawn_Of_The_Same_Segment_Is_Idempotent_Without_A_Document_Unique_Index()
    {
        // #481: Document no longer carries a unique (OriginDocumentId, OriginConstituentKey) index -- idempotency
        // now lives entirely on the DocumentSegment ledger (unique (SourceDocumentId, SegmentKey) + the Status
        // transition). Drive DerivedDocumentSpawner.SpawnAsync directly, twice, for the SAME still-Pending-looking
        // segment snapshot -- mirroring a sequential retry of DocumentSegmentationJob's spawn path. The second call's
        // reload sees Status == Spawned (set by the first call's markSpawned) and aborts cleanly: nothing is
        // inserted, and exactly one derived Document exists.
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        var spawner = GetRequiredService<DerivedDocumentSpawner>();

        DocumentSegment segment = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            segment = NewSegment(containerId, "Invoice A first", 0);
            await _segmentRepository.InsertAsync(segment, autoSave: true);
        });

        async Task<DocumentSegment?> ReloadClaimableAsync()
        {
            var entity = await _segmentRepository.FindAsync(segment.Id);
            return entity is { Status: DocumentSegmentStatus.Pending } ? entity : null;
        }

        Task MarkSpawnedAsync(DocumentSegment entity, Guid derivedId)
        {
            entity.MarkSpawned(derivedId);
            return _segmentRepository.UpdateAsync(entity);
        }

        var fileOrigin = new FileOrigin(
            blobName: $"blobs/{containerId:N}.pdf", uploadedByUserName: "test-user",
            contentType: "application/pdf", contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
            fileSize: 2048, originalFileName: "bundle.pdf");

        var firstId = await spawner.SpawnAsync<DocumentSegment>(
            containerId, tenantId: null, segment.SegmentKey, fileOrigin, ReloadClaimableAsync, MarkSpawnedAsync);
        var secondId = await spawner.SpawnAsync<DocumentSegment>(
            containerId, tenantId: null, segment.SegmentKey, fileOrigin, ReloadClaimableAsync, MarkSpawnedAsync);

        firstId.ShouldNotBeNull();
        secondId.ShouldBeNull();

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(1));
    }

    private async Task<Guid> ArrangeContainerAsync(
        string markdown, bool asContainer = true, string? markedMarkdown = null)
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

            // #381: the unified pass reads Document.Markdown, which now carries the inline *[Image OCR]*…*[End OCR]*
            // figure markers (no separate marked artifact). A figure test supplies that marked content via
            // markedMarkdown and it becomes Document.Markdown; a non-figure test just passes plain markdown.
            var documentMarkdown = markedMarkdown ?? markdown;
            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, [documentMarkdown]);

            // The segmentation job only runs for actual containers; mark it unless the test wants a doc that was
            // reclassified away from container (IsContainer == false).
            if (asContainer)
            {
                typeof(Document)
                    .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(doc, null);
            }

            await _documentRepository.InsertAsync(doc, autoSave: true);
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
            segmentKey: ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(sliceText)),
            sliceText: sliceText,
            ordinal: ordinal,
            kind: DocumentSegmentKind.Text);

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
