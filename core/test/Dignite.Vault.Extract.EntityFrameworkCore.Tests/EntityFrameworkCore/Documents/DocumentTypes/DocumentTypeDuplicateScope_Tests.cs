using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Duplicates;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentTypeDuplicateScopeTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // IBackgroundJobManager is substituted so the enqueue itself can be asserted (and so nothing actually
        // runs a reconciliation pass inside the test).
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #651: the save path's own obligations — round-tripping <c>DuplicateScope</c> through the type's CRUD surface,
/// enqueueing reconciliation <b>only</b> when the setting actually moved, and the pre-save preview counts.
/// Against the real DB, because the preview's two counts are index-served aggregates meeting ABP's ambient
/// <c>ISoftDelete</c> / <c>IMultiTenant</c> filters, which a substituted repository could not exercise at all.
/// </summary>
public class DocumentTypeDuplicateScope_Tests : VaultExtractTestBase<DocumentTypeDuplicateScopeTestModule>
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-000000000651");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-000000000651");
    private static readonly Guid Carol = Guid.Parse("33333333-3333-3333-3333-000000000651");

    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly DuplicateScopeReconciliationJob _reconciliationJob;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentTypeDuplicateScope_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _reconciliationJob = GetRequiredService<DuplicateScopeReconciliationJob>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Create_Defaults_To_Layer_And_Enqueues_Nothing()
    {
        var dto = await WithUnitOfWorkAsync(() => _documentTypeAppService.CreateAsync(new CreateDocumentTypeDto
        {
            TypeCode = "invoice.default",
            DisplayName = "Invoice"
        }));

        dto.DuplicateScope.ShouldBe(DuplicateDetectionScope.Layer);

        // A brand-new type has no documents, so there is no persisted verdict to bring in line.
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DuplicateScopeReconciliationArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Update_Enqueues_Reconciliation_Only_When_The_Scope_Actually_Changed()
    {
        var created = await ArrangeTypeAsync("invoice.switch", DuplicateDetectionScope.Layer);

        // An edit that leaves the scope alone must not sweep the whole type.
        await WithUnitOfWorkAsync(() => _documentTypeAppService.UpdateAsync(created.Id, new UpdateDocumentTypeDto
        {
            TypeCode = "invoice.switch",
            DisplayName = "Invoice (renamed)",
            ConfidenceThreshold = 0.9,
            DuplicateScope = DuplicateDetectionScope.Layer
        }));

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DuplicateScopeReconciliationArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());

        // The switch itself does.
        var updated = await WithUnitOfWorkAsync(() => _documentTypeAppService.UpdateAsync(created.Id, new UpdateDocumentTypeDto
        {
            TypeCode = "invoice.switch",
            DisplayName = "Invoice (renamed)",
            ConfidenceThreshold = 0.9,
            DuplicateScope = DuplicateDetectionScope.Uploader
        }));

        updated.DuplicateScope.ShouldBe(DuplicateDetectionScope.Uploader);
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DuplicateScopeReconciliationArgs>(a =>
                a.DocumentTypeId == created.Id && a.TenantId == null && a.AfterId == null),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    /// <summary>
    /// #651 5: the preview is the real diff, not a direction heuristic. The corpus below is built so that a
    /// narrowing switch does BOTH things at once, which is what a heuristic ("narrowing only clears") gets
    /// wrong: Bob's lone copy stops being a duplicate of Alice's, while Carol's pair — unflagged because the
    /// first of them was extracted before the second existed — starts being one.
    /// </summary>
    [Fact]
    public async Task Preview_Counts_What_Would_Be_Flagged_And_Cleared_Under_The_Prospective_Scope()
    {
        var created = await ArrangeTypeAsync("invoice.preview", DuplicateDetectionScope.Layer);

        // Alice's two copies collide under either scope -> no change.
        await ArrangeDocumentAsync(created.Id, "fp-alice", Alice, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-alice", Alice, flagged: true);
        // Bob shares Alice's OTHER key, so layer-wide he is a duplicate and is flagged; per uploader he is not.
        await ArrangeDocumentAsync(created.Id, "fp-shared", Alice, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-shared", Bob, flagged: true);
        // Carol's pair is stale the other way: two copies, one uploader, neither flagged.
        await ArrangeDocumentAsync(created.Id, "fp-carol", Carol, flagged: false);
        await ArrangeDocumentAsync(created.Id, "fp-carol", Carol, flagged: false);
        // No key at all, and an operator-allowed document: neither is ever re-evaluated.
        await ArrangeDocumentAsync(created.Id, fingerprint: null, Carol, flagged: false);
        await ArrangeDocumentAsync(created.Id, "fp-carol", Carol, flagged: false, allowed: true);

        var preview = await WithUnitOfWorkAsync(() =>
            _documentTypeAppService.GetDuplicateScopePreviewAsync(created.Id, DuplicateDetectionScope.Uploader));

        preview.CurrentScope.ShouldBe(DuplicateDetectionScope.Layer);
        preview.ProspectiveScope.ShouldBe(DuplicateDetectionScope.Uploader);
        preview.WouldChange.ShouldBeTrue();
        preview.WillFlagCount.ShouldBe(2);    // Carol's pair
        preview.WillClearCount.ShouldBe(2);   // Alice's and Bob's fp-shared copies, now alone in their buckets
    }

    [Fact]
    public async Task Preview_Of_The_Scope_Already_In_Force_Changes_Nothing()
    {
        var created = await ArrangeTypeAsync("invoice.noop", DuplicateDetectionScope.Layer);
        await ArrangeDocumentAsync(created.Id, "fp-1", Alice, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-1", Bob, flagged: true);

        var preview = await WithUnitOfWorkAsync(() =>
            _documentTypeAppService.GetDuplicateScopePreviewAsync(created.Id, DuplicateDetectionScope.Layer));

        preview.WouldChange.ShouldBeFalse();
        preview.WillFlagCount.ShouldBe(0);
        preview.WillClearCount.ShouldBe(0);
    }

    /// <summary>
    /// The promise the preview makes: the numbers the admin is shown are the documents the job then actually
    /// moves. Same data, preview first, then the real reconciliation pass — counted from the persisted bits
    /// before and after, not from anything the preview said.
    /// </summary>
    [Fact]
    public async Task Preview_Matches_What_The_Job_Then_Changes()
    {
        var created = await ArrangeTypeAsync("invoice.parity", DuplicateDetectionScope.Layer);
        await ArrangeDocumentAsync(created.Id, "fp-alice", Alice, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-alice", Alice, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-shared", Alice, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-shared", Bob, flagged: true);
        await ArrangeDocumentAsync(created.Id, "fp-carol", Carol, flagged: false);
        await ArrangeDocumentAsync(created.Id, "fp-carol", Carol, flagged: false);

        var preview = await WithUnitOfWorkAsync(() =>
            _documentTypeAppService.GetDuplicateScopePreviewAsync(created.Id, DuplicateDetectionScope.Uploader));

        var before = await ReadFlagsAsync(created.Id);

        // Save the switch the preview was about, then run the pass it enqueues.
        await WithUnitOfWorkAsync(() => _documentTypeAppService.UpdateAsync(created.Id, new UpdateDocumentTypeDto
        {
            TypeCode = "invoice.parity",
            DisplayName = "Invoice",
            ConfidenceThreshold = 0.7,
            DuplicateScope = DuplicateDetectionScope.Uploader
        }));

        await _reconciliationJob.ExecuteAsync(new DuplicateScopeReconciliationArgs
        {
            DocumentTypeId = created.Id,
            TenantId = null
        });

        var after = await ReadFlagsAsync(created.Id);

        var actuallyFlagged = after.Count(kv => kv.Value && !before[kv.Key]);
        var actuallyCleared = after.Count(kv => !kv.Value && before[kv.Key]);

        actuallyFlagged.ShouldBe((int)preview.WillFlagCount);
        actuallyCleared.ShouldBe((int)preview.WillClearCount);
        // And the diff is not vacuously zero on both sides.
        (actuallyFlagged + actuallyCleared).ShouldBeGreaterThan(0);
    }

    /// <summary>The persisted duplicate bit of every LIVE document of the type, by id.</summary>
    private async Task<Dictionary<Guid, bool>> ReadFlagsAsync(Guid documentTypeId)
    {
        return await WithUnitOfWorkAsync(async () =>
        {
            var documents = await _documentRepository.GetListAsync(d => d.DocumentTypeId == documentTypeId);
            return documents.ToDictionary(
                d => d.Id,
                d => (d.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None);
        });
    }

    private async Task<DocumentTypeDto> ArrangeTypeAsync(string typeCode, DuplicateDetectionScope duplicateScope)
        => await WithUnitOfWorkAsync(() => _documentTypeAppService.CreateAsync(new CreateDocumentTypeDto
        {
            TypeCode = typeCode,
            DisplayName = "Invoice",
            DuplicateScope = duplicateScope
        }));

    private async Task ArrangeDocumentAsync(
        Guid documentTypeId,
        string? fingerprint,
        Guid? creatorId = null,
        bool flagged = false,
        bool allowed = false)
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(documentId, tenantId: null, DocumentTestData.NewFileOrigin(documentId));
            DocumentTestData.MarkClassified(document, documentTypeId);
            document.SetFieldFingerprint(fingerprint);
            if (flagged)
            {
                document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
            }

            if (allowed)
            {
                document.AllowDuplicate();
            }

            if (creatorId.HasValue)
            {
                // ABP's audit setter only fills CreatorId when it is still null, so an explicit uploader
                // survives the insert — which is what makes the Uploader-scoped buckets below distinguishable.
                typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
            }

            await _documentRepository.InsertAsync(document, autoSave: true);
        });
    }
}
