using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Segments;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(ExtractEntityFrameworkCoreTestModule))]
public class ContainerReclassifyRetractionTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // DocumentAppService.ReclassifyAsync / the segmentation sink touch these collaborators; substitute the
        // out-of-process ones so the test exercises the real DB + the real ContainerMarkerClearedEventHandler.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<ExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #349 integration tests: reclassifying an already-segmented container to a concrete type must retract its spawned
/// sub-documents (soft-delete + a <see cref="DocumentDeletedEto"/> per sub-document) and remove the container's
/// <see cref="DocumentSegment"/> rows, while the container itself becomes a normal concrete-typed document. Runs
/// against the real SQLite DB so the <c>ContainerMarkerClearedEvent</c> local event actually dispatches to
/// <c>ContainerMarkerClearedEventHandler</c> on UoW completion.
/// <para>
/// <b>Scope of these assertions.</b> <see cref="IDistributedEventBus"/> is an NSubstitute substitute, so these tests
/// verify, precisely: (1) the local event dispatches to the handler on the reclassify UoW's completion; (2) the four
/// retraction post-conditions hold against the real DB — the container becomes a concrete-typed non-container, its
/// Text sub-documents are soft-deleted, and its Text segment rows are gone; (3) <c>PublishAsync</c> is invoked exactly
/// once per retracted sub-document (and never when there are none). They do <b>not</b> assert that those publishes
/// enrolled in the same transactional outbox as the soft-deletes, nor that publish + soft-delete are atomic — the
/// substitute records calls but does not exercise ABP's real outbox, so outbox atomicity is out of scope here (it is a
/// framework guarantee; see the all-in-one-UoW note in <c>ContainerMarkerClearedEventHandler</c>).
/// </para>
/// </summary>
public class ContainerReclassifyRetraction_Tests
    : ExtractTestBase<ContainerReclassifyRetractionTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IRepository<DocumentType, Guid> _documentTypeRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;

    public ContainerReclassifyRetraction_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
        _documentTypeRepository = GetRequiredService<IRepository<DocumentType, Guid>>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Reclassifying_A_Segmented_Container_Retracts_Sub_Documents_And_Segments()
    {
        var typeId = await SeedDocumentTypeAsync("invoice.general");
        var containerId = await ArrangeSegmentedContainerAsync(subDocumentCount: 2);

        // Reclassify the container to a concrete type (the operator-correction path).
        var result = await _appService.ReclassifyAsync(
            containerId, new ReclassifyDocumentInput { DocumentTypeId = typeId });

        // (d) The container is now a concrete-typed, non-container document.
        result.IsContainer.ShouldBeFalse();
        result.DocumentTypeCode.ShouldBe("invoice.general");

        await WithUnitOfWorkAsync(async () =>
        {
            var container = await _documentRepository.GetAsync(containerId);
            container.IsContainer.ShouldBeFalse();
            container.DocumentTypeId.ShouldBe(typeId);

            // (a) The sub-documents are soft-deleted (no longer visible under the ambient ISoftDelete filter).
            (await _documentRepository.GetListByOriginAsync(containerId)).ShouldBeEmpty();

            // (c) The container's segment rows are gone.
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
        });

        // (b) PublishAsync was invoked exactly once per sub-document (call count only; outbox enrolment / atomicity
        // with the soft-deletes is not asserted here — see the class summary).
        await _eventBus.Received(2).PublishAsync(Arg.Any<DocumentDeletedEto>());
    }

    [Fact]
    public async Task Reclassifying_A_Non_Segmented_Container_Publishes_No_Retraction_Events()
    {
        var typeId = await SeedDocumentTypeAsync("invoice.general");
        var containerId = await ArrangeSegmentedContainerAsync(subDocumentCount: 0);

        await _appService.ReclassifyAsync(
            containerId, new ReclassifyDocumentInput { DocumentTypeId = typeId });

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(containerId)).IsContainer.ShouldBeFalse());

        // No sub-documents existed, so no retraction events fire (the marker-cleared handler is a no-op here).
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentDeletedEto>());
    }

    [Fact]
    public async Task Reclassifying_A_Container_Retracts_Only_Text_Children_Leaving_Figure_Children_Intact()
    {
        // #364/#371: under the unified ledger (#371) a container can ALSO carry FIGURE-kind sub-documents — a
        // genuinely embedded document (an invoice photo inside what is now a concrete-typed contract). Reclassifying
        // the container to a concrete type must retract only the TEXT-kind (bundle-constituent) children; the
        // FIGURE-kind child (and its Kind.Figure segment row) must survive, exactly as a freshly-uploaded concrete
        // document with an embedded figure keeps it, instead of being blanket-deleted by an OriginDocumentId sweep.
        var typeId = await SeedDocumentTypeAsync("invoice.general");
        var containerId = await ArrangeSegmentedContainerAsync(subDocumentCount: 2);
        var (figureChildId, figureSegmentId) = await ArrangeFigureChildAsync(containerId);

        await _appService.ReclassifyAsync(
            containerId, new ReclassifyDocumentInput { DocumentTypeId = typeId });

        await WithUnitOfWorkAsync(async () =>
        {
            // Only the figure-kind child survives under the OriginDocumentId; the two text children are gone.
            var survivors = await _documentRepository.GetListByOriginAsync(containerId);
            survivors.Count.ShouldBe(1);
            survivors[0].Id.ShouldBe(figureChildId);

            // The figure-kind segment row is untouched (kept as the surviving child's provenance), still Spawned and
            // still pointing at its surviving child; the two text segment rows are removed.
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(1);
            segments[0].Id.ShouldBe(figureSegmentId);
            segments[0].Kind.ShouldBe(DocumentSegmentKind.Figure);
            segments[0].Status.ShouldBe(DocumentSegmentStatus.Spawned);
            segments[0].RoutedDocumentId.ShouldBe(figureChildId);
        });

        // DocumentDeletedEto fired for exactly the two text children — never for the figure-kind child.
        await _eventBus.Received(2).PublishAsync(Arg.Any<DocumentDeletedEto>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Is<DocumentDeletedEto>(e => e.DocumentId == figureChildId));
    }

    private async Task<Guid> SeedDocumentTypeAsync(string typeCode)
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, typeCode, typeCode), autoSave: true));
        return typeId;
    }

    /// <summary>
    /// Seeds a container document (Markdown set, IsContainer marked) plus <paramref name="subDocumentCount"/> derived
    /// TEXT-kind sub-documents (OriginDocumentId == container) and one Spawned segment row each — mirroring the
    /// post-segmentation state produced by <c>DocumentSegmentationJob</c>'s derived-document spawn
    /// (<c>DerivedDocumentSpawner</c>).
    /// </summary>
    private async Task<Guid> ArrangeSegmentedContainerAsync(int subDocumentCount)
    {
        var containerId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var container = new Document(
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
                .Invoke(container, ["Invoice A first\nInvoice B second"]);

            typeof(Document)
                .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(container, null);

            await _documentRepository.InsertAsync(container, autoSave: true);

            for (var i = 0; i < subDocumentCount; i++)
            {
                var sliceText = $"Invoice slice {i}";
                var segmentKey = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(sliceText));
                var derivedId = _guidGenerator.Create();

                var derived = Document.CreateDerived(
                    derivedId,
                    tenantId: null,
                    fileOrigin: new FileOrigin(
                        blobName: $"{derivedId:N}.md",
                        uploadedByUserName: "test-user",
                        contentType: "text/markdown",
                        contentHash: segmentKey,
                        fileSize: sliceText.Length,
                        originalFileName: $"segment-{i}.md"),
                    originDocumentId: containerId,
                    originConstituentKey: segmentKey);
                await _documentRepository.InsertAsync(derived, autoSave: true);

                var segment = new DocumentSegment(
                    _guidGenerator.Create(), tenantId: null, sourceDocumentId: containerId,
                    segmentKey: segmentKey, sliceText: sliceText, ordinal: i, kind: DocumentSegmentKind.Text);
                segment.MarkSpawned(derivedId);
                await _segmentRepository.InsertAsync(segment, autoSave: true);
            }
        });

        return containerId;
    }

    /// <summary>
    /// Seeds one #371 FIGURE-kind sub-document onto an existing container: a Spawned <see cref="DocumentSegment"/> row
    /// with <see cref="DocumentSegmentKind.Figure"/> plus its derived <see cref="Document"/> (OriginDocumentId ==
    /// container). A figure span (an embedded image that is a standalone document) is orthogonal to container-ness, so
    /// a container can carry these alongside its text segmentation children, and they survive a container→type
    /// reclassify. Returns the derived figure-child id and the figure segment row id.
    /// </summary>
    private async Task<(Guid FigureChildId, Guid FigureSegmentId)> ArrangeFigureChildAsync(Guid containerId)
    {
        var figureChildId = _guidGenerator.Create();
        var figureSegmentId = _guidGenerator.Create();
        var figureKey = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes("INVOICE No 42 Total 100"));

        await WithUnitOfWorkAsync(async () =>
        {
            var derived = Document.CreateDerived(
                figureChildId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"{figureChildId:N}.md",
                    uploadedByUserName: "test-user",
                    contentType: "text/markdown",
                    contentHash: figureKey,
                    fileSize: 23,
                    originalFileName: "segment-figure.md"),
                originDocumentId: containerId,
                originConstituentKey: figureKey);
            await _documentRepository.InsertAsync(derived, autoSave: true);

            var segment = new DocumentSegment(
                figureSegmentId, tenantId: null, sourceDocumentId: containerId,
                segmentKey: figureKey, sliceText: "INVOICE No 42 Total 100",
                ordinal: 99, kind: DocumentSegmentKind.Figure, pageNumber: 1);
            segment.MarkSpawned(figureChildId);
            await _segmentRepository.InsertAsync(segment, autoSave: true);
        });

        return (figureChildId, figureSegmentId);
    }
}
