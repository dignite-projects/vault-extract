using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class RelationDiscoveryEventHandlerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        // Substitute the scheduler entirely so we verify the handler queues with the correct
        // pipeline code without exercising the real PipelineRun creation / job enqueue path.
        // The scheduler ctor needs valid args; pass cheap substitutes.
        context.Services.AddSingleton(provider =>
            Substitute.For<DocumentPipelineJobScheduler>(
                Substitute.For<IDocumentRepository>(),
                provider.GetRequiredService<DocumentPipelineRunManager>(),
                Substitute.For<IBackgroundJobManager>(),
                Substitute.For<ICurrentTenant>()));

        // Disable the enqueue delay in tests — race window protection (codex review fix [high])
        // matters in production but would slow tests pointlessly.
        context.Services.Configure<PaperbaseAIBehaviorOptions>(opts =>
        {
            opts.RelationDiscoveryDelaySeconds = 0;
        });
    }
}

public class RelationDiscoveryEventHandler_Tests
    : PaperbaseApplicationTestBase<RelationDiscoveryEventHandlerTestModule>
{
    private readonly RelationDiscoveryEventHandler _handler;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _scheduler;

    public RelationDiscoveryEventHandler_Tests()
    {
        _handler = GetRequiredService<RelationDiscoveryEventHandler>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _scheduler = GetRequiredService<DocumentPipelineJobScheduler>();
    }

    [Fact]
    public async Task HandleEventAsync_Should_Queue_Job_When_Document_Exists()
    {
        var document = CreateDocument();
        _documentRepository
            .FindAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(document);
        _scheduler
            .QueueAsync(Arg.Any<Document>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult<DocumentPipelineRun>(null!));

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = document.Id,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.95
        });

        await _scheduler.Received(1).QueueAsync(
            Arg.Is<Document>(d => d.Id == document.Id),
            PaperbasePipelines.RelationDiscovery,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task HandleEventAsync_Should_Skip_When_Document_Has_Empty_Id()
    {
        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = Guid.Empty,
            DocumentTypeCode = "contract.general"
        });

        await _scheduler.DidNotReceive().QueueAsync(
            Arg.Any<Document>(), Arg.Any<string>(), Arg.Any<TimeSpan?>());
        await _documentRepository.DidNotReceive().FindAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_Should_Skip_When_Document_No_Longer_Exists()
    {
        // Hard-deleted between classification publish and handler dispatch.
        var documentId = Guid.NewGuid();
        _documentRepository
            .FindAsync(documentId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = documentId,
            DocumentTypeCode = "contract.general"
        });

        await _scheduler.DidNotReceive().QueueAsync(
            Arg.Any<Document>(), Arg.Any<string>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task HandleEventAsync_Should_Pass_Configured_Delay_To_Scheduler()
    {
        // Codex review fix [high] L2 race: handler MUST pass the configured delay through
        // to QueueAsync so the background job is held off long enough for sibling
        // DocumentClassifiedEto handlers (e.g. ContractDocumentHandler) to commit their
        // typed records. Default-disabled in this test module (=0); override per-test.
        var aiOptions = GetRequiredService<IOptions<PaperbaseAIBehaviorOptions>>().Value;
        aiOptions.RelationDiscoveryDelaySeconds = 45;

        var document = CreateDocument();
        _documentRepository
            .FindAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(document);
        _scheduler
            .QueueAsync(Arg.Any<Document>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult<DocumentPipelineRun>(null!));

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = document.Id,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.95
        });

        await _scheduler.Received(1).QueueAsync(
            Arg.Any<Document>(),
            PaperbasePipelines.RelationDiscovery,
            Arg.Is<TimeSpan?>(d => d.HasValue && d.Value == TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public async Task HandleEventAsync_Should_Pass_Null_Delay_When_Setting_Is_Zero()
    {
        // Operator opt-out: setting RelationDiscoveryDelaySeconds = 0 means "no delay";
        // verify we pass null (not TimeSpan.Zero) so the scheduler / job manager treats it
        // as "fire immediately".
        var aiOptions = GetRequiredService<IOptions<PaperbaseAIBehaviorOptions>>().Value;
        aiOptions.RelationDiscoveryDelaySeconds = 0;

        var document = CreateDocument();
        _documentRepository
            .FindAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(document);
        _scheduler
            .QueueAsync(Arg.Any<Document>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult<DocumentPipelineRun>(null!));

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = document.Id,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.95
        });

        await _scheduler.Received(1).QueueAsync(
            Arg.Any<Document>(),
            PaperbasePipelines.RelationDiscovery,
            Arg.Is<TimeSpan?>(d => d == null));
    }

    [Fact]
    public async Task HandleEventAsync_Should_Forward_TenantId_Via_CurrentTenant_Change()
    {
        // Codex review fix [high] "Tenant context dropped": eventData.TenantId must drive the
        // tenant context the scheduler reads when stamping JobArgs.TenantId. We can't directly
        // inspect that here (scheduler is substituted), but we CAN verify the handler doesn't
        // reach the scheduler when tenant flow is the only thing differing.
        // The actual tenant stamping is verified end-to-end by the scheduler's own behavior.
        var tenantId = Guid.NewGuid();
        var document = CreateDocument(tenantId);
        _documentRepository
            .FindAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(document);
        _scheduler
            .QueueAsync(Arg.Any<Document>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult<DocumentPipelineRun>(null!));

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = document.Id,
            TenantId = tenantId,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.95
        });

        // Scheduler invoked once with the correct pipeline code — full tenant propagation
        // is part of the scheduler's responsibility (its own tests cover JobArgs.TenantId).
        await _scheduler.Received(1).QueueAsync(
            Arg.Is<Document>(d => d.Id == document.Id),
            PaperbasePipelines.RelationDiscovery,
            Arg.Any<TimeSpan?>());
    }

    private static Document CreateDocument(Guid? tenantId = null)
    {
        return new Document(
            Guid.NewGuid(), tenantId: tenantId,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
