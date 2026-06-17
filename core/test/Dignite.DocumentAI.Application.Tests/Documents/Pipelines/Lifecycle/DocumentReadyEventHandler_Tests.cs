using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Pipelines.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.DocumentAI.Documents;

[DependsOn(typeof(DocumentAIApplicationTestModule))]
public class DocumentReadyEventHandlerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// DocumentReadyEventHandler behavior tests: verifies that it emits DocumentReadyEto according to the
/// CLAUDE.md "exit event contract" when lifecycle transitions to Ready, and ignores other transitions.
/// </summary>
public class DocumentReadyEventHandler_Tests
    : DocumentAIApplicationTestBase<DocumentReadyEventHandlerTestModule>
{
    private readonly DocumentReadyEventHandler _handler;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDistributedEventBus _eventBus;

    public DocumentReadyEventHandler_Tests()
    {
        _handler = GetRequiredService<DocumentReadyEventHandler>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Ready_Transition_Publishes_DocumentReadyEto()
    {
        var doc = CreateDocument(documentTypeCode: "contract.general");
        SetupDocumentRepository(doc);
        // #207: handler resolves TypeCode from DocumentTypeId for the ETO.
        _documentTypeRepository
            .FindAsync(TypeId("contract.general"), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentType(TypeId("contract.general"), null, "contract.general", "Contract"));

        var evt = new DocumentLifecycleStatusChangedEvent(
            doc.Id, DocumentLifecycleStatus.Processing, DocumentLifecycleStatus.Ready);

        await _handler.HandleEventAsync(evt);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentReadyEto>(e =>
                e.DocumentId == doc.Id &&
                e.TenantId == doc.TenantId &&
                e.DocumentTypeCode == "contract.general"),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task Container_Ready_Publishes_Eto_With_Marker_And_Null_Type()
    {
        // #346: a container reaches Ready with no type. The ETO must carry IsContainer=true and
        // DocumentTypeCode=null so downstream skips building a record from the container.
        var doc = CreateContainerDocument();
        SetupDocumentRepository(doc);

        var evt = new DocumentLifecycleStatusChangedEvent(
            doc.Id, DocumentLifecycleStatus.Processing, DocumentLifecycleStatus.Ready);

        await _handler.HandleEventAsync(evt);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentReadyEto>(e =>
                e.DocumentId == doc.Id &&
                e.IsContainer &&
                e.DocumentTypeCode == null),
            Arg.Any<bool>());

        // A container has no DocumentTypeId, so the handler must not even attempt a type lookup.
        await _documentTypeRepository.DidNotReceive().FindAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Non_Ready_Transition_Does_Not_Publish()
    {
        var doc = CreateDocument(documentTypeCode: null);
        SetupDocumentRepository(doc);

        var evt = new DocumentLifecycleStatusChangedEvent(
            doc.Id, DocumentLifecycleStatus.Processing, DocumentLifecycleStatus.Failed);

        await _handler.HandleEventAsync(evt);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentReadyEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Missing_Document_Does_Not_Publish()
    {
        var missingId = Guid.NewGuid();
        _documentRepository
            .FindAsync(missingId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        var evt = new DocumentLifecycleStatusChangedEvent(
            missingId, DocumentLifecycleStatus.Processing, DocumentLifecycleStatus.Ready);

        await _handler.HandleEventAsync(evt);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentReadyEto>(), Arg.Any<bool>());
    }

    private void SetupDocumentRepository(Document doc)
    {
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument(string? documentTypeCode)
    {
        var doc = new Document(
            Guid.NewGuid(), null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        if (!string.IsNullOrEmpty(documentTypeCode))
        {
            // Use the internal channel to write DocumentTypeId, the high-confidence path; #207 classification
            // result is the internal Id.
            typeof(Document)
                .GetMethod("ApplyAutomaticClassificationResult",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(doc, [TypeId(documentTypeCode), 0.99]);
        }

        return doc;
    }

    private static Document CreateContainerDocument()
    {
        var doc = CreateDocument(documentTypeCode: null);
        typeof(Document)
            .GetMethod("MarkAsContainer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, null);
        return doc;
    }

    private static Guid TypeId(string typeCode)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("type:" + typeCode)));
}
