using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
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
/// DocumentReadyEventHandler 行为测试：验证它在 lifecycle 跃迁到 Ready 时按
/// CLAUDE.md "出口事件契约" 发出 DocumentReadyEto，其他跃迁忽略。
/// </summary>
public class DocumentReadyEventHandler_Tests
    : PaperbaseApplicationTestBase<DocumentReadyEventHandlerTestModule>
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
        // #207：handler 由 DocumentTypeId 解析 TypeCode 填进 ETO。
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
            $"blobs/{Guid.NewGuid():N}.pdf",
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        if (!string.IsNullOrEmpty(documentTypeCode))
        {
            // 走 internal 通道写入 DocumentTypeId（高置信度路径；#207 分类结果是内部 Id）
            typeof(Document)
                .GetMethod("ApplyAutomaticClassificationResult",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(doc, [TypeId(documentTypeCode), 0.99]);
        }

        return doc;
    }

    private static Guid TypeId(string typeCode)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("type:" + typeCode)));
}
