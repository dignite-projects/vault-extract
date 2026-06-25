using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
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
    : ExtractApplicationTestBase<DocumentReadyEventHandlerTestModule>
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
    public async Task Container_Ready_Does_Not_Publish_Eto()
    {
        // #346 (Codex review): a container has no confirmed type, so it is not a consumable document — the handler
        // suppresses its DocumentReadyEto (downstream consumes only the sub-documents' own Ready events). It still
        // reaches Ready lifecycle for the operator UI.
        var doc = CreateContainerDocument();
        SetupDocumentRepository(doc);

        var evt = new DocumentLifecycleStatusChangedEvent(
            doc.Id, DocumentLifecycleStatus.Processing, DocumentLifecycleStatus.Ready);

        await _handler.HandleEventAsync(evt);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentReadyEto>(), Arg.Any<bool>());

        // A container short-circuits before the type lookup.
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
