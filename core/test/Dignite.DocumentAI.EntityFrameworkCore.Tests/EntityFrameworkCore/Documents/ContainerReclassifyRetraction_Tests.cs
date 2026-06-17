using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Segments;
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

namespace Dignite.DocumentAI.EntityFrameworkCore.Documents;

[DependsOn(typeof(DocumentAIEntityFrameworkCoreTestModule))]
public class ContainerReclassifyRetractionTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // DocumentAppService.ReclassifyAsync / the segmentation sink touch these collaborators; substitute the
        // out-of-process ones so the test exercises the real DB + the real ContainerMarkerClearedEventHandler.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #349 integration tests: reclassifying an already-segmented container to a concrete type must retract its spawned
/// sub-documents (soft-delete + a <see cref="DocumentDeletedEto"/> per sub-document) and remove the container's
/// <see cref="DocumentSegment"/> rows, while the container itself becomes a normal concrete-typed document. Runs
/// against the real SQLite DB so the <c>ContainerMarkerClearedEvent</c> local event actually dispatches to
/// <c>ContainerMarkerClearedEventHandler</c> on UoW completion.
/// </summary>
public class ContainerReclassifyRetraction_Tests
    : DocumentAITestBase<ContainerReclassifyRetractionTestModule>
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

        // (b) A DocumentDeletedEto was published per sub-document.
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
    /// sub-documents (OriginDocumentId == container) and one Spawned segment row each — mirroring the post-segmentation
    /// state produced by <c>DocumentSegmentationJob.CommitSpawnAsync</c>.
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
                var segmentKey = ContentHasher.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(sliceText));
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
                    segmentKey: segmentKey, sliceText: sliceText, ordinal: i);
                segment.MarkSpawned(derivedId);
                await _segmentRepository.InsertAsync(segment, autoSave: true);
            }
        });

        return containerId;
    }
}
