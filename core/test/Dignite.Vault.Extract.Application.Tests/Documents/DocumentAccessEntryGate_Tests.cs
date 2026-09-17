using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Reprocessing;
using Dignite.Vault.Extract.Permissions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Content;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 1, the half that closes the escapes: <b>entry gates every operation of the documents domain,
/// including the module-wide-only ones.</b>
/// <para>
/// Before this, "does this method check entry" was exactly "was this method touched by #632" — the projection of
/// a change set, not a design. <c>PermanentDeleteAsync</c>, <c>RetryPipelineAsync</c>, the four
/// <c>Reprocessing</c> methods, <c>ExportAsync</c> (a class-level attribute plus a read scope that deliberately
/// did not assert entry) and the untyped <c>UploadAsync</c> branch each carried only their own permission, so a
/// principal holding that permission without <c>Documents</c> was admitted to an area it could not open.
/// </para>
/// <para>
/// One refusal fact each, all with the same shape as #632's review used: the caller holds the operation's own
/// module-wide permission and <b>not</b> <c>Documents</c>. Each then grants entry and shows the same call gets
/// past authorization, so the refusal cannot be passing for some unrelated reason.
/// </para>
/// </summary>
public class DocumentAccessEntryGate_Tests : DocumentAccessTestBase
{
    private readonly IDocumentExportAppService _exportAppService;
    private readonly IDocumentReprocessingAppService _reprocessingAppService;

    public DocumentAccessEntryGate_Tests()
    {
        _exportAppService = GetRequiredService<IDocumentExportAppService>();
        _reprocessingAppService = GetRequiredService<IDocumentReprocessingAppService>();
    }

    // ===================== The five escapes =====================

    [Fact]
    public async Task PermanentDeleteAsync_refuses_the_permission_holder_without_entry()
    {
        var document = StubDocument(TypeA.Id, creatorId: StrangerId);
        DocumentRepository.AnyByOriginAsync(document.Id).Returns(false);
        Grant(VaultExtractPermissions.Documents.PermanentDelete);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => AppService.PermanentDeleteAsync(document.Id)));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.PermanentDelete);

        await AsStrangerAsync(() => AppService.PermanentDeleteAsync(document.Id));
    }

    [Fact]
    public async Task RetryPipelineAsync_refuses_the_permission_holder_without_entry()
    {
        var document = StubDocument(TypeA.Id, creatorId: StrangerId);
        await FailParseAsync(document);
        Grant(VaultExtractPermissions.Documents.Pipelines.Retry);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(() =>
            AppService.RetryPipelineAsync(
                document.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse })));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Pipelines.Retry);

        await AsStrangerAsync(() => AppService.RetryPipelineAsync(
            document.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse }));
    }

    [Fact]
    public async Task PreviewFieldExtractionAsync_refuses_the_permission_holder_without_entry()
    {
        Grant(VaultExtractPermissions.Documents.Reprocessing.FieldExtraction);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(
            () => _reprocessingAppService.PreviewFieldExtractionAsync(TypeA.Id)));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Reprocessing.FieldExtraction);

        (await AsStrangerAsync(() => _reprocessingAppService.PreviewFieldExtractionAsync(TypeA.Id)))
            .DocumentTypeId.ShouldBe(TypeA.Id);
    }

    [Fact]
    public async Task StartFieldExtractionAsync_refuses_the_permission_holder_without_entry()
    {
        var input = new StartFieldReextractionInput { DocumentTypeId = TypeA.Id };
        Grant(VaultExtractPermissions.Documents.Reprocessing.FieldExtraction);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(
            () => _reprocessingAppService.StartFieldExtractionAsync(input)));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Reprocessing.FieldExtraction);

        (await AsStrangerAsync(() => _reprocessingAppService.StartFieldExtractionAsync(input))).ShouldNotBeNull();
    }

    [Fact]
    public async Task PreviewReclassificationAsync_refuses_the_permission_holder_without_entry()
    {
        var input = new ReclassificationScopeInput { Scope = ReclassificationScope.AllDocuments };
        Grant(VaultExtractPermissions.Documents.Reprocessing.Reclassification);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(
            () => _reprocessingAppService.PreviewReclassificationAsync(input)));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Reprocessing.Reclassification);

        (await AsStrangerAsync(() => _reprocessingAppService.PreviewReclassificationAsync(input))).ShouldNotBeNull();
    }

    [Fact]
    public async Task StartReclassificationAsync_refuses_the_permission_holder_without_entry()
    {
        var input = new ReclassificationScopeInput { Scope = ReclassificationScope.AllDocuments };
        Grant(VaultExtractPermissions.Documents.Reprocessing.Reclassification);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(
            () => _reprocessingAppService.StartReclassificationAsync(input)));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Reprocessing.Reclassification);

        (await AsStrangerAsync(() => _reprocessingAppService.StartReclassificationAsync(input))).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExportAsync_refuses_the_permission_holder_without_entry()
    {
        StubQueryable(NewDocument(TypeA.Id, creatorId: StrangerId));
        var input = new ExportDocumentsInput { DocumentTypeCode = TypeA.TypeCode, Format = ExportFormat.Csv };
        Grant(VaultExtractPermissions.Documents.Export);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => _exportAppService.ExportAsync(input)));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Export);

        (await AsStrangerAsync(() => _exportAppService.ExportAsync(input))).ShouldNotBeNull();
    }

    /// <summary>
    /// The untyped upload branch: #629 gated it with a bare <c>CheckPolicyAsync(ConfirmClassification)</c> that
    /// never went through the checker and so never asserted entry. It is the DeclareType rule with an empty
    /// subject now, which reduces to exactly the same permission — plus entry.
    /// </summary>
    [Fact]
    public async Task An_untyped_UploadAsync_refuses_the_permission_holder_without_entry()
    {
        Grant(
            VaultExtractPermissions.Documents.Upload,
            VaultExtractPermissions.Documents.ConfirmClassification);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => AppService.UploadAsync(NewUpload())));

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Upload,
            VaultExtractPermissions.Documents.ConfirmClassification);

        (await AsStrangerAsync(() => AppService.UploadAsync(NewUpload()))).ShouldNotBeNull();
    }

    /// <summary>
    /// The overview statistics, which is also where the list page's review-queue badge count comes from. It was
    /// already <c>ReadAll</c>-gated; #635 adds entry alongside, as a row on the table.
    /// </summary>
    [Fact]
    public async Task The_overview_statistics_refuse_the_permission_holder_without_entry()
    {
        var statistics = GetRequiredService<IDocumentStatisticsAppService>();
        DocumentRepository.GetStatisticsAsync().ReturnsForAnyArgs(new DocumentStatisticsModel());
        Grant(VaultExtractPermissions.Documents.ReadAll);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(() => statistics.GetAsync()));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        (await AsStrangerAsync(() => statistics.GetAsync())).ShouldNotBeNull();
    }

    // ===================== Anonymous callers =====================

    /// <summary>
    /// The three documents-domain services carry no <c>[Authorize]</c> at all any more, not even a class-level
    /// one, and <c>VaultExtractAppService</c> declares none either — so "must be authenticated" is not asserted
    /// anywhere by attribute. It does not need to be: ABP's permission value providers key on the principal's
    /// user / role / client claims, and an anonymous principal has none, so entry is simply not granted and the
    /// first checker call refuses. This fact pins that, because the reasoning is not visible at any call site.
    /// </summary>
    [Fact]
    public async Task An_anonymous_caller_is_refused_even_though_no_Authorize_attribute_remains()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        StubQueryable(document);

        // The grant set is what a fully-permissioned principal would carry; the caller is simply not one.
        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.ReadAll,
            VaultExtractPermissions.Documents.Delete);
        Authorization.Granted.Clear();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AppService.GetAsync(document.Id));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AppService.GetListAsync(new GetDocumentListInput()));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AppService.DeleteAsync(document.Id));
    }

    // ===================== UpdateCabinetAsync moved from Read to Edit =====================

    /// <summary>
    /// <c>UpdateCabinetAsync</c> calls <c>SetCabinet</c> + <c>UpdateAsync</c>; filing it under the Read rule meant
    /// a Read grant was no longer read-only.
    /// <para>
    /// The positive halves are separate facts rather than a second call in this one, because
    /// <c>DocumentTypeGrantMap</c> is resolved once per scope by design — a grant handed out after the first
    /// check of a request is deliberately not seen by that request, and the whole test shares one scope.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UpdateCabinetAsync_refuses_a_Read_grant_holder()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);

        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(() =>
            AppService.UpdateCabinetAsync(document.Id, new UpdateDocumentCabinetInput { CabinetId = null })));
    }

    [Fact]
    public async Task UpdateCabinetAsync_admits_an_Edit_grant_holder()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);

        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id, StrangerId);

        var dto = await AsStrangerAsync(() =>
            AppService.UpdateCabinetAsync(document.Id, new UpdateDocumentCabinetInput { CabinetId = null }));

        dto.Id.ShouldBe(document.Id);
    }

    // ===================== RetryPipelineAsync moved onto the Edit grant =====================

    /// <summary>
    /// #635's revisit of #632's "module-wide only, by decision": retry's per-type arm is the <c>Edit</c> grant,
    /// the same one <c>RerecognizeAsync</c> beside it already used. A <c>Read</c> grant does not reach it.
    /// </summary>
    [Fact]
    public async Task RetryPipelineAsync_refuses_a_Read_grant_holder()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        await FailParseAsync(document);

        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsStrangerAsync(() =>
            AppService.RetryPipelineAsync(
                document.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse })));
    }

    /// <summary>
    /// The module-wide arm of the Retry rule is <c>Pipelines.Retry</c>, per the Issue's table and its prose
    /// ("the permission <c>Pipelines.Retry</c> keeps its meaning as the module-wide arm"), so a holder of it
    /// reaches every document of the layer — including a type they hold only a <c>Read</c> grant on.
    /// <para>
    /// <b>Recorded because the Issue is not self-consistent here.</b> Its acceptance list also asks that
    /// <c>RetryPipelineAsync</c> "refuses a <c>Read</c>-grant holder with <c>Pipelines.Retry</c>", which the
    /// table it is derived from cannot produce: a module-wide permission that did not admit every type would not
    /// be a module-wide arm. The table is implemented as written and this fact states the consequence, so the
    /// disagreement is visible in the code rather than only in a review comment. What #635 <i>does</i> change for
    /// this principal is that entry is now required alongside — asserted by
    /// <see cref="RetryPipelineAsync_refuses_the_permission_holder_without_entry"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RetryPipelineAsync_admits_a_module_wide_Pipelines_Retry_holder_on_any_type()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        await FailParseAsync(document);

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Pipelines.Retry);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        await AsStrangerAsync(() => AppService.RetryPipelineAsync(
            document.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse }));

        var retry = await GetRequiredService<IDocumentPipelineRunRepository>()
            .FindLatestByDocumentAndCodeAsync(document.Id, VaultExtractPipelines.Parse);
        retry!.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    [Fact]
    public async Task RetryPipelineAsync_is_admitted_by_an_Edit_grant_on_the_documents_type()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        await FailParseAsync(document);

        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id, StrangerId);

        await AsStrangerAsync(() => AppService.RetryPipelineAsync(
            document.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse }));

        var retry = await GetRequiredService<IDocumentPipelineRunRepository>()
            .FindLatestByDocumentAndCodeAsync(document.Id, VaultExtractPipelines.Parse);
        retry!.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    // ===================== A conditional owner arm has no scope =====================

    /// <summary>
    /// A scope is a row predicate and has no term for "is this document under review" — the review state is on
    /// the row, not on the caller. Resolving a scope for a rule whose owner arm is
    /// <see cref="DocumentOwnerArm.UnlessUnderReview"/> would therefore silently widen it to every document the
    /// caller uploaded, locked ones included, which is precisely the hole the arm exists to close. It throws
    /// instead of answering.
    /// <para>
    /// Only <see cref="DocumentAccessRule.Read"/> is ever asked in production, and its arm is unconditional by
    /// design; this fact is what keeps a future caller from reaching for the wrong one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResolveScopeAsync_refuses_a_rule_whose_owner_arm_depends_on_the_documents_review_state()
    {
        var checker = GetRequiredService<DocumentAccessChecker>();
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        foreach (var rule in new[] { DocumentAccessRule.Edit, DocumentAccessRule.Retry })
        {
            var exception = await Should.ThrowAsync<AbpException>(
                () => AsStrangerAsync(() => checker.ResolveScopeAsync(rule)));
            exception.Message.ShouldContain(nameof(DocumentOwnerArm.UnlessUnderReview));
        }

        // The counter-case: the rule the production paths actually resolve still answers.
        (await AsStrangerAsync(() => checker.ResolveScopeAsync(DocumentAccessRule.Read)))
            .IsUnrestricted.ShouldBeTrue();
    }

    // ===================== The per-request cost of the grant map =====================

    /// <summary>
    /// #635 decision 4's cost claim: one request performs at most <b>one</b> type-layer read and <b>one</b>
    /// multi-name grant check per type of the layer, whatever it asks — the list, the recycle bin and the detail
    /// page alike. Before this the checker swept the layer once per permission name per shape, and the recycle
    /// bin swept it twice.
    /// <para>
    /// Counted through a decorator over ABP's real <c>IResourcePermissionChecker</c>, not through a stand-in, so
    /// the number counted is the number the production path actually makes. The single-name overload is asserted
    /// to be unused: a fall back to it would still answer correctly while quietly restoring the per-name sweep.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("recycle-bin")]
    [InlineData("detail")]
    public async Task One_request_costs_at_most_one_grant_check_per_type_and_one_type_layer_read(string surface)
    {
        var document = StubDocument(TypeA.Id, creatorId: StrangerId, deleted: surface == "recycle-bin");
        StubQueryable(document);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        CheckCounter.Reset();
        DocumentTypeRepository.ClearReceivedCalls();

        await AsStrangerAsync(async () =>
        {
            switch (surface)
            {
                case "list":
                    await AppService.GetListAsync(new GetDocumentListInput());
                    break;
                case "recycle-bin":
                    await AppService.GetListAsync(new GetDocumentListInput { IsDeleted = true });
                    break;
                default:
                    await AppService.GetAsync(document.Id);
                    break;
            }
        });

        // Two types in this layer.
        CheckCounter.MultiNameChecks.ShouldBeLessThanOrEqualTo(2);
        CheckCounter.SingleNameChecks.ShouldBe(0);

        // The layer sweep is the parameterless GetListAsync overload; the by-predicate one is the DTO reference
        // map, a different question.
        await DocumentTypeRepository.Received(1).GetListAsync(
            Arg.Any<bool>(), Arg.Any<System.Threading.CancellationToken>());
    }

    // ===================== helpers =====================

    private async Task FailParseAsync(Document document)
    {
        var manager = GetRequiredService<DocumentPipelineRunManager>();
        var run = await manager.StartAsync(document, VaultExtractPipelines.Parse);
        await manager.FailAsync(document, run, errorMessage: "parse failed");
    }

    private static UploadDocumentInput NewUpload()
        => new()
        {
            File = new RemoteStreamContent(
                new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("entry gate body")),
                "entry-gate.txt",
                "text/plain")
        };
}
