using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.EventBus.Distributed;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Code review on #623 (2026-09-05), root-cause fix: <see cref="DocumentAppService.ApplyManualClassificationAsync"/>
/// (backing <see cref="IDocumentAppService.ConfirmClassificationAsync"/> / <see cref="IDocumentAppService.ReclassifyAsync"/>)
/// must refuse to run before <see cref="Document.Markdown"/> is set -- mirroring the same guard already on
/// <see cref="DocumentAppService.RerecognizeAsync"/> / <see cref="DocumentAppService.ReextractFieldsAsync"/>
/// (see <see cref="DocumentAppService_Rerecognize_Tests"/>). Without it, an operator could confirm a type on a
/// document whose Parse has not written Markdown yet; the cascade field extraction would then run over an empty
/// body, and since MissingRequiredFields is non-blocking, the document could reach Ready with no fields at all.
/// This guard is also what makes the Parse-cascade declared-type branch (<c>DocumentParseBackgroundJob.CompleteRunAsync</c>)
/// race-free: no Classification run can exist before Parse writes Markdown.
/// <para>
/// Reuses <see cref="DocumentAppServiceReviewTestModule"/>'s dependency set (already wires
/// <c>IDocumentRepository</c> / <c>IDocumentTypeRepository</c> / <c>IBackgroundJobManager</c> /
/// <c>IDistributedEventBus</c> the way <see cref="DocumentAppService.ApplyManualClassificationAsync"/> needs them).
/// </para>
/// </summary>
public class DocumentAppService_ManualClassificationGuard_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;

    public DocumentAppService_ManualClassificationGuard_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task ConfirmClassificationAsync_Throws_NotTextExtracted_When_No_Markdown()
    {
        var doc = CreateDocument();
        StubGet(doc);
        var targetType = StubType();

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.ConfirmClassificationAsync(doc.Id, new ConfirmClassificationInput { DocumentTypeId = targetType.Id }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.NotTextExtracted);

        await AssertNoClassificationSideEffectsAsync(doc.Id);
    }

    [Fact]
    public async Task ReclassifyAsync_Throws_NotTextExtracted_When_No_Markdown()
    {
        var doc = CreateDocument();
        StubGet(doc);
        var targetType = StubType();

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.ReclassifyAsync(doc.Id, new ReclassifyDocumentInput { DocumentTypeId = targetType.Id }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.NotTextExtracted);

        await AssertNoClassificationSideEffectsAsync(doc.Id);
    }

    private async Task AssertNoClassificationSideEffectsAsync(Guid documentId)
    {
        // No Classification run was ever created for this document.
        var classificationRun = await _runRepository.FindLatestByDocumentAndCodeAsync(
            documentId, VaultExtractPipelines.Classification);
        classificationRun.ShouldBeNull();

        // No #527 §8 field-extraction cascade was enqueued.
        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DocumentFieldExtractionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());

        // No DocumentClassifiedEto was published.
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentClassifiedEto>(), Arg.Any<bool>());
    }

    /// <summary>
    /// The target type has to RESOLVE for these facts to still be about the Markdown guard. #632 moved the
    /// target-type permission check ahead of that guard, so the target lookup that feeds it moved with it —
    /// existence is still validated before permission, which now means an unknown target id answers
    /// EntityNotFound before NotTextExtracted rather than after. An unresolvable id is an invalid request
    /// whatever the document's processing state; these two facts are about the state, so they supply a real one.
    /// </summary>
    private DocumentType StubType()
    {
        var type = new DocumentType(Guid.NewGuid(), tenantId: null, "guard.target", "Guard Target");
        _documentTypeRepository.FindAsync(type.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
        return type;
    }

    private void StubGet(Document doc)
    {
        // ApplyManualClassificationAsync loads via FindWithFieldValuesAsync (#527: field-stage loader).
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
