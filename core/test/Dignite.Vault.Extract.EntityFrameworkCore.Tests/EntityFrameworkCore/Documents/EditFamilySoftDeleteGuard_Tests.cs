using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Review;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class EditFamilySoftDeleteGuardTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // None of the twelve methods under test reach blob storage, background jobs or the event bus --
        // EnsureNotDeleted (or the ISoftDelete filter, for Case A) refuses them first -- but the wider
        // DocumentAppService constructor graph still needs them resolvable (mirrors DocumentRestoreConflict_Tests).
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #635 acceptance: <b>no Edit-family method acts on a soft-deleted document.</b> Two layers protect this, and
/// both are pinned here against the real SQLite DB (not a mocked repository), because the primary layer is a
/// global EF query filter that a mocked <see cref="IDocumentRepository"/> cannot exercise at all.
/// <para>
/// <b>Case A, the layer that actually protects production:</b> every method loads its document through
/// <c>_documentRepository.GetAsync</c> / <c>FindWithFieldValuesAsync</c> with ABP's global <see cref="ISoftDelete"/>
/// filter active. A soft-deleted document is invisible to that query, so the load itself throws
/// <see cref="EntityNotFoundException"/> -- the method body, including <c>EnsureNotDeleted</c>, is never reached.
/// </para>
/// <para>
/// <b>Case B, defence in depth:</b> with the caller's own <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c> scope
/// active (the shape the recycle-bin list already runs in), the load succeeds and
/// <c>DocumentAppService.EnsureNotDeleted</c> is the only thing left standing between the soft-deleted row and a
/// write. It throws <c>BusinessException(VaultExtractErrorCodes.Document.InRecycleBin)</c>.
/// </para>
/// <para>
/// This host grants everything (<c>AddAlwaysAllowAuthorization</c>, inherited through
/// <see cref="VaultExtractApplicationTestModule"/> -&gt; <c>VaultExtractDomainTestModule</c> -&gt;
/// <c>VaultExtractTestBaseModule</c>): authorization is not what this test is about, and a refusal from either
/// case must be attributable to the soft-delete guard alone.
/// </para>
/// </summary>
public class EditFamilySoftDeleteGuard_Tests : VaultExtractTestBase<EditFamilySoftDeleteGuardTestModule>
{
    private const string BaselineMarkdown = "# original body, never meant to change";
    private const string BaselineAmountValue = "100";

    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IDataFilter _dataFilter;
    private readonly IGuidGenerator _guidGenerator;

    public EditFamilySoftDeleteGuard_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _dataFilter = GetRequiredService<IDataFilter>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    /// <summary>
    /// One row per Edit-family method, each carrying inputs valid enough to clear the method's own input
    /// validation and reach the document load. The target <c>DocumentTypeId</c> Confirm/Reclassify carry is a
    /// fresh, never-seeded id on purpose: both methods only look it up AFTER <c>EnsureNotDeleted</c>, so neither
    /// Case A nor Case B ever reaches that lookup, and a real type would only obscure what is actually gating.
    /// </summary>
    public static TheoryData<string, Func<IDocumentAppService, Guid, Task>> EditFamilyMethods => new()
    {
        {
            nameof(IDocumentAppService.UpdateExtractedFieldsAsync),
            (svc, id) => svc.UpdateExtractedFieldsAsync(
                id, new UpdateExtractedFieldsInput { Fields = new Dictionary<string, JsonElement>() })
        },
        {
            nameof(IDocumentAppService.UpdateMarkdownAsync),
            (svc, id) => svc.UpdateMarkdownAsync(id, new UpdateMarkdownInput { Markdown = "# attempted correction" })
        },
        {
            nameof(IDocumentAppService.ReparseAsync),
            (svc, id) => svc.ReparseAsync(id)
        },
        {
            nameof(IDocumentAppService.ReextractFieldsAsync),
            (svc, id) => svc.ReextractFieldsAsync(id)
        },
        {
            nameof(IDocumentAppService.RetryPipelineAsync),
            (svc, id) => svc.RetryPipelineAsync(id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse })
        },
        {
            nameof(IDocumentAppService.ConfirmClassificationAsync),
            (svc, id) => svc.ConfirmClassificationAsync(id, new ConfirmClassificationInput { DocumentTypeId = Guid.NewGuid() })
        },
        {
            nameof(IDocumentAppService.ReclassifyAsync),
            (svc, id) => svc.ReclassifyAsync(id, new ReclassifyDocumentInput { DocumentTypeId = Guid.NewGuid() })
        },
        {
            nameof(IDocumentAppService.RejectReviewAsync),
            (svc, id) => svc.RejectReviewAsync(id, new RejectReviewInput { Reason = "attempted" })
        },
        {
            nameof(IDocumentAppService.AllowDuplicateAsync),
            (svc, id) => svc.AllowDuplicateAsync(id)
        },
        {
            nameof(IDocumentAppService.ResolveFieldValidationWarningsAsync),
            (svc, id) => svc.ResolveFieldValidationWarningsAsync(
                id, new ResolveFieldValidationWarningsInput { FieldDefinitionIds = [] })
        },
        {
            nameof(IDocumentAppService.ConfirmFieldEntryAsync),
            (svc, id) => svc.ConfirmFieldEntryAsync(id)
        },
        {
            nameof(IDocumentAppService.UpdateCabinetAsync),
            (svc, id) => svc.UpdateCabinetAsync(id, new UpdateDocumentCabinetInput { CabinetId = null })
        }
    };

    [Theory]
    [MemberData(nameof(EditFamilyMethods))]
    public async Task No_Edit_family_method_acts_on_a_soft_deleted_document(
        string methodName, Func<IDocumentAppService, Guid, Task> invoke)
    {
        var (documentId, cabinetId, typeId) = await SeedSoftDeletedDocumentAsync();
        var baseline = await ReadIgnoringSoftDeleteAsync(documentId);

        // ---- Case A: the default ambient filter (ISoftDelete ON) -- the row is invisible to the load itself. ----
        await Should.ThrowAsync<EntityNotFoundException>(() => invoke(_appService, documentId));

        AssertUnchanged(baseline, await ReadIgnoringSoftDeleteAsync(documentId), $"{methodName}, Case A (filter on)");

        // ---- Case B: the caller disables the filter itself -- EnsureNotDeleted is the only thing left. ----
        var exception = await Should.ThrowAsync<BusinessException>(() => WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                await invoke(_appService, documentId);
            }
        }));
        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.InRecycleBin, methodName);

        AssertUnchanged(
            baseline, await ReadIgnoringSoftDeleteAsync(documentId), $"{methodName}, Case B (filter disabled by caller)");

        // Sanity: the seeded row itself must still carry the values a mutation would have to disturb -- an empty
        // baseline (cabinet/type/fields all null) would let a broken guard pass by coincidence.
        cabinetId.ShouldNotBe(Guid.Empty);
        typeId.ShouldNotBe(Guid.Empty);
    }

    private async Task<(Guid DocumentId, Guid CabinetId, Guid TypeId)> SeedSoftDeletedDocumentAsync()
    {
        var typeId = _guidGenerator.Create();
        var cabinetId = _guidGenerator.Create();
        var documentId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, "soft-delete.guard", "Soft Delete Guard"), autoSave: true);
            await _cabinetRepository.InsertAsync(
                new Cabinet(cabinetId, tenantId: null, "Soft Delete Guard Cabinet"), autoSave: true);

            var document = new Document(
                documentId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{documentId:N}.pdf",
                    uploadedByUserName: "guard-test",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    fileSize: 1024,
                    originalFileName: "guard.pdf"));
            document.SetMarkdown(BaselineMarkdown);
            document.ApplyAutomaticClassificationResult(typeId, classificationConfidence: 1.0);
            document.SetFlexFields(new Dictionary<string, object?> { ["amount"] = BaselineAmountValue });
            document.SetCabinet(cabinetId);
            document.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: true);

            await _documentRepository.InsertAsync(document, autoSave: true);
            await _documentRepository.DeleteAsync(documentId, autoSave: true);
        });

        return (documentId, cabinetId, typeId);
    }

    private Task<Document> ReadIgnoringSoftDeleteAsync(Guid id) => WithUnitOfWorkAsync(async () =>
    {
        using (_dataFilter.Disable<ISoftDelete>())
        {
            return await _documentRepository.GetAsync(id, includeDetails: true);
        }
    });

    private static void AssertUnchanged(Document baseline, Document actual, string because)
    {
        actual.ReviewReasons.ShouldBe(baseline.ReviewReasons, because);
        actual.Markdown.ShouldBe(baseline.Markdown, because);
        actual.CabinetId.ShouldBe(baseline.CabinetId, because);
        actual.DocumentTypeId.ShouldBe(baseline.DocumentTypeId, because);
        // Compared through a JSON snapshot rather than CLR equality on the bag's boxed values, which may not
        // round-trip to the same runtime type on reload -- the snapshot is what a real drift would change either way.
        JsonSerializer.Serialize(actual.FlexFields).ShouldBe(JsonSerializer.Serialize(baseline.FlexFields), because);
    }
}
