using System;
using System.Reflection;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.DocumentAI.EntityFrameworkCore.Documents;

[DependsOn(typeof(DocumentAIEntityFrameworkCoreTestModule))]
public class ReclassifyToContainerSignalTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Substitute the out-of-process event bus so the test can assert on what the real
        // ContainerMarkerSetEventHandler publishes; everything else runs against the real DB.
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #355 integration tests: re-recognizing an already concrete-typed document as a <b>container</b> must publish a
/// <see cref="DocumentReclassifiedToContainerEto"/> so downstream retracts the record it built from the former type.
/// Runs against the real SQLite DB so the <c>ContainerMarkerSetEvent</c> local event actually dispatches to
/// <c>ContainerMarkerSetEventHandler</c> on UoW completion. Mirror of <see cref="ContainerReclassifyRetraction_Tests"/>
/// (the container→type direction).
/// <para>
/// <see cref="IDistributedEventBus"/> is an NSubstitute substitute, so these tests verify that the local event
/// dispatches to the handler on UoW completion and that <c>PublishAsync</c> is invoked exactly once (and only on a
/// real type→container transition). Outbox enrolment / atomicity is a framework guarantee, out of scope here.
/// </para>
/// </summary>
public class ReclassifyToContainerSignal_Tests
    : DocumentAITestBase<ReclassifyToContainerSignalTestModule>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentType, Guid> _documentTypeRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public ReclassifyToContainerSignal_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IRepository<DocumentType, Guid>>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    [Fact]
    public async Task Re_Recognizing_A_Typed_Document_As_Container_Publishes_The_Retraction_Eto()
    {
        var typeId = await SeedDocumentTypeAsync("invoice.general");
        var documentId = await ArrangeClassifiedDocumentAsync(typeId);

        // The document is re-recognized as a container (the marker flips false→true while it still has a type).
        await MarkAsContainerInUowAsync(documentId);

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId);
            document.IsContainer.ShouldBeTrue();
            document.DocumentTypeId.ShouldBeNull();
        });

        // The local event dispatched on UoW completion and the handler published exactly one retraction ETO.
        await _eventBus.Received(1).PublishAsync(Arg.Is<DocumentReclassifiedToContainerEto>(
            e => e.DocumentId == documentId));
    }

    [Fact]
    public async Task Fresh_Document_First_Detected_As_Container_Publishes_No_Retraction_Eto()
    {
        // A never-classified document (no prior type) detected as a container: no downstream record existed.
        var documentId = await ArrangeUnclassifiedDocumentAsync();

        await MarkAsContainerInUowAsync(documentId);

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetAsync(documentId)).IsContainer.ShouldBeTrue());

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentReclassifiedToContainerEto>());
    }

    private async Task<Guid> SeedDocumentTypeAsync(string typeCode)
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, typeCode, typeCode), autoSave: true));
        return typeId;
    }

    private async Task<Guid> ArrangeClassifiedDocumentAsync(Guid typeId)
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var document = NewDocument(documentId);
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, typeId);
            await _documentRepository.InsertAsync(document, autoSave: true);
        });
        return documentId;
    }

    private async Task<Guid> ArrangeUnclassifiedDocumentAsync()
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
            await _documentRepository.InsertAsync(NewDocument(documentId), autoSave: true));
        return documentId;
    }

    private Document NewDocument(Guid id)
        => new(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                fileSize: 1024,
                originalFileName: "test.pdf"));

    /// <summary>
    /// Loads the document and flips its container marker inside one UoW, mirroring the classification job's
    /// container branch: the <c>ContainerMarkerSetEvent</c> local event dispatches when this UoW completes.
    /// </summary>
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
}
