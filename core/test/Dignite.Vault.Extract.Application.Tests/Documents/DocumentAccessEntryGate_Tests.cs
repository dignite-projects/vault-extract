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
    /// The untyped upload: #629 gated it with a bare <c>CheckPolicyAsync(ConfirmClassification)</c> that never
    /// went through the checker and so never asserted entry. Since #645 it is the Upload rule on an empty subject,
    /// which reduces to <c>Documents.Upload</c> — plus entry.
    /// </summary>
    [Fact]
    public async Task An_untyped_UploadAsync_refuses_the_permission_holder_without_entry()
    {
        Grant(VaultExtractPermissions.Documents.Upload);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => AppService.UploadAsync(NewUpload())));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

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

    // ===================== A caller granted nothing =====================

    /// <summary>
    /// With <b>no</b> permission granted, the three entry points refuse — the read path, the list and a mutating
    /// method alike, so the refusal does not depend on which of the three shapes the call happens to take.
    /// <para>
    /// <b>What this does not prove.</b> It was written as "an anonymous caller is refused", which it cannot show:
    /// <see cref="GrantSetAuthorizationService"/> answers from a grant set and never looks at the principal, so an
    /// anonymous caller and a fully-authenticated one with an empty grant set are the same thing here. The real
    /// claim — that ABP's permission value providers key on the principal's claims, so a caller with no identity
    /// is granted nothing — needs the real permission pipeline, and lives in
    /// <c>Mcp.Integration.Tests/Documents/AnonymousCaller_Tests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_caller_granted_nothing_is_refused_on_every_shape()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        StubQueryable(document);

        Grant();

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
    /// <c>DocumentAccessMemo</c> is resolved once per scope by design — a grant handed out after the first
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

    // ===================== The per-request cost of the access memo =====================

    /// <summary>
    /// #635 decision 4's cost claim, per surface. A <b>set</b> of documents costs one type-layer read and one
    /// multi-name grant check per type; a <b>single</b> document costs neither the read nor more than one check,
    /// because judging one document asks about one type.
    /// <para>
    /// Counted through a decorator over ABP's real <c>IResourcePermissionChecker</c>, not through a stand-in, so
    /// the number counted is the number the production path actually makes. The single-name overload is asserted
    /// to be unused: a fall back to it would still answer correctly while quietly restoring the per-name sweep.
    /// The layer has four types and every fact reaches at most one, so a per-type sweep cannot pass a
    /// per-document assertion by coincidence.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("recycle-bin")]
    public async Task A_list_request_costs_one_grant_check_per_type_and_one_type_layer_read(string surface)
    {
        var document = StubDocument(TypeA.Id, creatorId: StrangerId, deleted: surface == "recycle-bin");
        StubQueryable(document);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        ResetCounters();

        await AsStrangerAsync(() => AppService.GetListAsync(
            new GetDocumentListInput { IsDeleted = surface == "recycle-bin" ? true : null }));

        CheckCounter.MultiNameChecks.ShouldBeLessThanOrEqualTo(LayerTypes.Length);
        CheckCounter.SingleNameChecks.ShouldBe(0);

        // The layer sweep is the parameterless GetListAsync overload; the by-predicate one is the DTO reference
        // map, a different question.
        await DocumentTypeRepository.Received(1).GetListAsync(
            Arg.Any<bool>(), Arg.Any<System.Threading.CancellationToken>());
    }

    /// <summary>
    /// An uploader reaching their own document: the ownership arm answers before the per-type arm is reached, so
    /// the authorization itself costs <b>no</b> grant check and <b>no</b> type-layer read. Under the first #635
    /// shape this same call resolved a whole scope and swept the layer.
    /// <para>
    /// <c>GetBlobAsync</c> rather than <c>GetAsync</c>, because it returns a stream and therefore does not
    /// project the per-row rights — see the next fact for what those cost.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reaching_ones_own_document_costs_no_grant_check_and_no_type_layer_read()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        BlobContainer.GetAsync(own.FileOrigin!.BlobName, Arg.Any<System.Threading.CancellationToken>())
            .Returns(_ => new System.IO.MemoryStream([1, 2, 3]));
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        ResetCounters();

        await AsOwnerAsync(() => AppService.GetBlobAsync(own.Id));

        CheckCounter.MultiNameChecks.ShouldBe(0);
        CheckCounter.SingleNameChecks.ShouldBe(0);
        await DocumentTypeRepository.DidNotReceive().GetListAsync(
            Arg.Any<bool>(), Arg.Any<System.Threading.CancellationToken>());
    }

    /// <summary>
    /// The owner's <b>detail</b> page costs exactly one grant check, and still no layer read. Five of the six
    /// rights are answered by the ownership arm; <c>canReview</c> is the one that is not — its rule's owner arm is
    /// <see cref="DocumentOwnerArm.Never"/> by design — so it reaches the per-type arm, and all four grants on
    /// that one type come back in that single multi-name call.
    /// </summary>
    [Fact]
    public async Task The_owners_own_detail_page_costs_one_grant_check_and_no_type_layer_read()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        ResetCounters();

        var rights = (await AsOwnerAsync(() => AppService.GetAsync(own.Id))).Rights;
        rights.CanEdit.ShouldBeTrue();
        rights.CanReview.ShouldBeFalse();

        CheckCounter.MultiNameChecks.ShouldBe(1);
        CheckCounter.SingleNameChecks.ShouldBe(0);
        await DocumentTypeRepository.DidNotReceive().GetListAsync(
            Arg.Any<bool>(), Arg.Any<System.Threading.CancellationToken>());
    }

    /// <summary>
    /// A module-wide <c>ReadAll</c> holder on somebody else's document: the module-wide arm answers Read,
    /// Delete, Restore and Retry outright, so the only grant check left is the one the remaining rules need on
    /// that document's own type — one, not four, and still no layer read.
    /// </summary>
    [Fact]
    public async Task A_ReadAll_holders_detail_page_costs_at_most_one_grant_check_and_no_type_layer_read()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        ResetCounters();

        await AsStrangerAsync(() => AppService.GetAsync(document.Id));

        CheckCounter.MultiNameChecks.ShouldBeLessThanOrEqualTo(1);
        CheckCounter.SingleNameChecks.ShouldBe(0);
        await DocumentTypeRepository.DidNotReceive().GetListAsync(
            Arg.Any<bool>(), Arg.Any<System.Threading.CancellationToken>());
    }

    /// <summary>
    /// The standard-permission half of the same claim: resolving a document's six rights names
    /// <c>Documents.Default</c> six times and <c>ConfirmClassification</c> twice, and the memo turns that into
    /// one check each. The bound is the number of DISTINCT permission names the whole request can reach, which is
    /// smaller than the number of times it asks.
    /// </summary>
    [Fact]
    public async Task A_detail_page_asks_each_standard_permission_name_at_most_once()
    {
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        ResetCounters();

        await AsStrangerAsync(() => AppService.GetAsync(document.Id));

        // Entry + the six rules' module-wide names, of which Edit and Review share one and — since #645 merged the
        // role-level restore permission into Documents.Delete — Delete and Restore share another:
        // Documents.Default, ReadAll, ConfirmClassification, Delete, Pipelines.Retry.
        Authorization.PolicyChecks.ShouldBeLessThanOrEqualTo(5);
    }

    private void ResetCounters()
    {
        CheckCounter.Reset();
        Authorization.ResetPolicyChecks();
        DocumentTypeRepository.ClearReceivedCalls();
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
