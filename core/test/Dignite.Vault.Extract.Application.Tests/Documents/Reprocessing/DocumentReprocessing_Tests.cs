using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Reprocessing;
using Dignite.Vault.Extract.Documents.Reprocessing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class ReprocessingTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
    }
}

/// <summary>Bulk reprocessing AppService (#289 steps 3 / 5): preview return, scope translation that protects manual confirmations / pending-review queues, and dispatcher enqueue.</summary>
public class DocumentReprocessingAppService_Tests
    : ExtractApplicationTestBase<ReprocessingTestModule>
{
    private readonly IDocumentReprocessingAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public DocumentReprocessingAppService_Tests()
    {
        _appService = GetRequiredService<IDocumentReprocessingAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();

        // Type exists in the current layer, so EnsureTypeInCurrentLayerAsync passes.
        _documentTypeRepository.FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => new DocumentType(ci.ArgAt<Guid>(0), null, "type.x", "Type X"));
        _documentRepository.CountForReprocessingAsync(
                Arg.Any<Guid?>(), Arg.Any<DocumentReviewReasons?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(7L);
    }

    [Fact]
    public async Task PreviewFieldExtraction_Returns_Count_And_FieldNames()
    {
        var typeId = Guid.NewGuid();
        _fieldDefinitionRepository.GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new(Guid.NewGuid(), null, typeId, "amount", "Amount", "p", FieldDataType.Number),
                new(Guid.NewGuid(), null, typeId, "party", "Party", "p", FieldDataType.Text)
            });

        var dto = await _appService.PreviewFieldExtractionAsync(typeId);

        dto.DocumentTypeId.ShouldBe(typeId);
        dto.DocumentCount.ShouldBe(7);
        dto.FieldNames.ShouldBe(new[] { "amount", "party" });
    }

    [Fact]
    public async Task StartFieldExtraction_Enqueues_Dispatcher_With_Type_And_Null_Cursor()
    {
        var typeId = Guid.NewGuid();

        var result = await _appService.StartFieldExtractionAsync(new StartFieldReextractionInput { DocumentTypeId = typeId });

        result.EstimatedDocumentCount.ShouldBe(7);
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentFieldReextractionDispatcherArgs>(a => a.DocumentTypeId == typeId && a.AfterId == null),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task StartReclassification_OnlyCurrentType_Without_Type_Throws_Validation()
    {
        await Should.ThrowAsync<AbpValidationException>(() =>
            _appService.StartReclassificationAsync(new ReclassificationScopeInput
            {
                Scope = ReclassificationScope.OnlyCurrentType,
                DocumentTypeId = null
            }));
    }

    [Fact]
    public async Task StartReclassification_AllDocuments_Default_Excludes_Manually_Confirmed()
    {
        await _appService.StartReclassificationAsync(new ReclassificationScopeInput
        {
            Scope = ReclassificationScope.AllDocuments,
            IncludeManuallyConfirmed = false
        });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentReclassificationDispatcherArgs>(a =>
                a.DocumentTypeId == null && a.WithReason == null && a.ExcludeManuallyConfirmed && a.AfterId == null),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task StartReclassification_IncludeManuallyConfirmed_Does_Not_Exclude()
    {
        await _appService.StartReclassificationAsync(new ReclassificationScopeInput
        {
            Scope = ReclassificationScope.AllDocuments,
            IncludeManuallyConfirmed = true
        });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentReclassificationDispatcherArgs>(a => !a.ExcludeManuallyConfirmed),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task StartReclassification_PendingReviewQueue_Targets_PendingReview_Without_Type()
    {
        await _appService.StartReclassificationAsync(new ReclassificationScopeInput
        {
            Scope = ReclassificationScope.PendingReviewQueue
        });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentReclassificationDispatcherArgs>(a =>
                a.DocumentTypeId == null && a.WithReason == DocumentReviewReasons.UnresolvedClassification && !a.ExcludeManuallyConfirmed),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }
}

/// <summary>Field re-extraction dispatcher (#289 step 4): chained self-continuation. A full batch enqueues single-document jobs plus the next dispatcher with a cursor; a partial batch stops.</summary>
public class DocumentFieldReextractionDispatcherJob_Tests
    : ExtractApplicationTestBase<ReprocessingTestModule>
{
    private readonly DocumentFieldReextractionDispatcherJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public DocumentFieldReextractionDispatcherJob_Tests()
    {
        _job = GetRequiredService<DocumentFieldReextractionDispatcherJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
    }

    [Fact]
    public async Task Full_Batch_Chains_Next_Dispatcher_Partial_Batch_Stops()
    {
        var typeId = Guid.NewGuid();
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;
        var firstBatch = Enumerable.Range(0, batchSize).Select(_ => Guid.NewGuid()).ToList();
        var firstBatchLastId = firstBatch[^1];
        var secondBatch = new List<Guid> { Guid.NewGuid() };

        _documentRepository
            .GetIdsForReprocessingAsync(typeId, null, false, (Guid?)null, batchSize, Arg.Any<CancellationToken>())
            .Returns(firstBatch);
        _documentRepository
            .GetIdsForReprocessingAsync(typeId, null, false, firstBatchLastId, batchSize, Arg.Any<CancellationToken>())
            .Returns(secondBatch);

        // First batch is full: batchSize single-document jobs plus one next dispatcher with cursor = last Id.
        await _job.ExecuteAsync(new DocumentFieldReextractionDispatcherArgs { DocumentTypeId = typeId, TenantId = null, AfterId = null });

        await _backgroundJobManager.Received(batchSize).EnqueueAsync(
            Arg.Any<DocumentFieldExtractionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentFieldReextractionDispatcherArgs>(a => a.DocumentTypeId == typeId && a.AfterId == firstBatchLastId),
            Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());

        _backgroundJobManager.ClearReceivedCalls();

        // Second batch is partial: one single-document job and no next dispatcher because the scope is exhausted.
        await _job.ExecuteAsync(new DocumentFieldReextractionDispatcherArgs { DocumentTypeId = typeId, TenantId = null, AfterId = firstBatchLastId });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Any<DocumentFieldExtractionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentFieldReextractionDispatcherArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }
}
