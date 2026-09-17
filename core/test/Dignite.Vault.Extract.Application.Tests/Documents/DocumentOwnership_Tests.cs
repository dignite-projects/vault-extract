using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Review;
using Dignite.Vault.Extract.Permissions;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 1: <b>whoever uploaded a document may view, edit and delete it.</b> The per-type Read / Edit /
/// Delete grants are what extend those rights to <i>other people's</i> documents of that type.
/// <para>
/// Every fact here runs as the shape the Issue opens with: a principal holding <c>Documents</c> (entry) plus an
/// <c>Upload</c> grant on type A, and <b>nothing else</b> — no <c>ReadAll</c>, no per-type Read / Edit / Delete,
/// no <c>ConfirmClassification</c>. Before #635 that principal could upload an invoice and then never see it
/// again.
/// </para>
/// </summary>
public class DocumentOwnership_Tests : DocumentAccessTestBase
{
    /// <summary>The Issue's day-one principal, in one place so every fact starts from the same grants.</summary>
    private void GrantUploaderOnTypeA()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);
        GrantResource(VaultExtractResourcePermissions.Upload, TypeA.Id);
    }

    // ===================== The four-way: read / edit / delete / restore on one's own =====================

    [Fact]
    public async Task An_uploader_reads_their_own_document_with_no_Read_grant_anywhere()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantUploaderOnTypeA();

        var dto = await AsOwnerAsync(() => AppService.GetAsync(own.Id));

        dto.Id.ShouldBe(own.Id);
        dto.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    [Fact]
    public async Task An_uploader_sees_their_own_document_in_the_list_and_the_total_counts_it()
    {
        var own = NewDocument(TypeA.Id, creatorId: OwnerId);
        StubQueryable(own, NewDocument(TypeA.Id, creatorId: StrangerId), NewDocument(TypeB.Id, creatorId: StrangerId));
        GrantUploaderOnTypeA();

        var page = await AsOwnerAsync(() => AppService.GetListAsync(new GetDocumentListInput()));

        // The count is asserted with the rows: a filter applied only after CountAsync would hand back a
        // correct-looking page whose total leaks how many documents the caller may not see.
        page.TotalCount.ShouldBe(1);
        page.Items.Count.ShouldBe(1);
        page.Items[0].Id.ShouldBe(own.Id);
    }

    /// <summary>
    /// Correcting one's own upload: reclassify to a type the caller may assign, correct the Markdown, and edit
    /// the field values. All three are the Edit rule, whose ownership arm is what admits them here — the caller
    /// holds no <c>Edit</c> grant and no <c>ConfirmClassification</c>.
    /// </summary>
    [Fact]
    public async Task An_uploader_edits_their_own_document_and_may_reclassify_it_to_a_type_they_may_assign()
    {
        var own = StubDocument(TypeB.Id, creatorId: OwnerId, markdown: "# body");
        GrantUploaderOnTypeA();

        // Reclassify: Edit on the current type (ownership) AND DeclareType on the target (the Upload grant on A).
        var reclassified = await AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id }));
        reclassified.DocumentTypeCode.ShouldBe(TypeA.TypeCode);

        var corrected = await AsOwnerAsync(() =>
            AppService.UpdateMarkdownAsync(own.Id, new UpdateMarkdownInput { Markdown = "# corrected" }));
        corrected.Markdown.ShouldBe("# corrected");

        var edited = await AsOwnerAsync(() => AppService.UpdateExtractedFieldsAsync(
            own.Id, new UpdateExtractedFieldsInput { Fields = new Dictionary<string, JsonElement>() }));
        edited.Id.ShouldBe(own.Id);
    }

    /// <summary>
    /// DeclareType has <b>no</b> ownership arm: owning a document is not a licence to move it into a type the
    /// caller was never granted. The same reclassify that succeeds to type A above is refused to type B.
    /// </summary>
    [Fact]
    public async Task An_uploader_may_not_reclassify_their_own_document_into_a_type_they_may_not_assign()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeB.Id })));
    }

    [Fact]
    public async Task An_uploader_retries_a_failed_pipeline_on_their_own_document()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        await FailParseAsync(own);
        GrantUploaderOnTypeA();

        await AsOwnerAsync(() => AppService.RetryPipelineAsync(
            own.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse }));

        var retry = await GetRequiredService<IDocumentPipelineRunRepository>()
            .FindLatestByDocumentAndCodeAsync(own.Id, VaultExtractPipelines.Parse);
        retry!.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    [Fact]
    public async Task An_uploader_soft_deletes_their_own_document_sees_it_in_the_recycle_bin_and_restores_it()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantUploaderOnTypeA();

        await AsOwnerAsync(() => AppService.DeleteAsync(own.Id));
        await DocumentRepository.Received(1).DeleteAsync(own.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // The bin: admission is entry, rows are the Read scope's soft-deleted documents — own included.
        own.IsDeleted = true;
        StubQueryable(own, NewDocument(TypeA.Id, creatorId: StrangerId, deleted: true));

        var bin = await AsOwnerAsync(
            () => AppService.GetListAsync(new GetDocumentListInput { IsDeleted = true }));
        bin.TotalCount.ShouldBe(1);
        bin.Items[0].Id.ShouldBe(own.Id);

        await AsOwnerAsync(() => AppService.RestoreAsync(own.Id));
        own.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_uploader_files_their_own_document_into_a_cabinet()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantUploaderOnTypeA();

        var dto = await AsOwnerAsync(() => AppService.UpdateCabinetAsync(
            own.Id, new UpdateDocumentCabinetInput { CabinetId = null }));

        dto.Id.ShouldBe(own.Id);
    }

    // ===================== Review is NOT open to the owner =====================

    /// <summary>
    /// #635 decision 2, the reason ownership is a per-rule flag: these three clear a blocking review reason, and
    /// that gate exists so somebody other than the uploader checks the uploader's work. A suspected duplicate
    /// invoice is exactly the adversarial case the review queue is for.
    /// </summary>
    [Fact]
    public async Task An_uploader_is_refused_all_three_review_resolutions_on_their_own_document()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.AllowDuplicateAsync(own.Id)));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.ResolveFieldValidationWarningsAsync(
                own.Id, new ResolveFieldValidationWarningsInput { FieldDefinitionIds = [Guid.NewGuid()] })));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.RejectReviewAsync(own.Id, new RejectReviewInput { Reason = "mine" })));
    }

    [Fact]
    public async Task An_Edit_grant_on_the_type_admits_all_three_review_resolutions()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantUploaderOnTypeA();
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id);

        (await AsOwnerAsync(() => AppService.AllowDuplicateAsync(own.Id))).Id.ShouldBe(own.Id);
        (await AsOwnerAsync(() => AppService.ResolveFieldValidationWarningsAsync(
            own.Id, new ResolveFieldValidationWarningsInput { FieldDefinitionIds = [] }))).Id.ShouldBe(own.Id);
        (await AsOwnerAsync(() =>
            AppService.RejectReviewAsync(own.Id, new RejectReviewInput { Reason = "x" }))).Id.ShouldBe(own.Id);
    }

    [Fact]
    public async Task The_module_wide_ConfirmClassification_admits_all_three_review_resolutions()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.ConfirmClassification);

        (await AsOwnerAsync(() => AppService.AllowDuplicateAsync(own.Id))).Id.ShouldBe(own.Id);
        (await AsOwnerAsync(() => AppService.ResolveFieldValidationWarningsAsync(
            own.Id, new ResolveFieldValidationWarningsInput { FieldDefinitionIds = [] }))).Id.ShouldBe(own.Id);
        (await AsOwnerAsync(() =>
            AppService.RejectReviewAsync(own.Id, new RejectReviewInput { Reason = "x" }))).Id.ShouldBe(own.Id);
    }

    // ===================== The owner lock: modification while under review =====================

    /// <summary>
    /// The review invariant, held where it can actually hold. Closing only the three explicit review methods to
    /// an owner does <b>not</b> keep an uploader from clearing a blocking reason on their own document, because
    /// the edit family clears the same bits as a side effect: this very call clears
    /// <see cref="DocumentReviewReasons.FieldExtractionIncomplete"/> outright (#491's escape path — an empty
    /// field set is enough), which releases the document to Ready and fires <c>DocumentReadyEto</c>.
    /// <para>
    /// So while a document carries a blocking reason other than classification, the ownership arm of the edit
    /// family is shut (<see cref="DocumentOwnerArm.UnlessUnderReview"/>). The clear itself stays — it is what a
    /// reviewer filling the fields in relies on — it is simply no longer reachable by the uploader.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_owner_may_not_fill_in_the_fields_of_their_own_document_blocked_on_incomplete_extraction()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.UpdateExtractedFieldsAsync(
                own.Id, new UpdateExtractedFieldsInput { Fields = new Dictionary<string, JsonElement>() })));

        own.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeTrue();
    }

    [Fact]
    public async Task An_Edit_grant_on_the_type_fills_in_the_fields_of_a_document_blocked_on_incomplete_extraction()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        GrantUploaderOnTypeA();
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id);

        await AsOwnerAsync(() => AppService.UpdateExtractedFieldsAsync(
            own.Id, new UpdateExtractedFieldsInput { Fields = new Dictionary<string, JsonElement>() }));

        // #491's escape path, intact for the reviewer: manual entry IS the resolution.
        own.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    [Fact]
    public async Task The_module_wide_ConfirmClassification_fills_in_the_fields_of_a_document_under_review()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.ConfirmClassification);

        await AsOwnerAsync(() => AppService.UpdateExtractedFieldsAsync(
            own.Id, new UpdateExtractedFieldsInput { Fields = new Dictionary<string, JsonElement>() }));

        own.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    /// <summary>
    /// The whole edit family, on a document blocked on a field-validation warning. Every one of these either
    /// replaces the warning set outright (re-extraction, a retried field-extraction run) or is a modification the
    /// lock covers by the same rule; <c>UpdateCabinetAsync</c> is on the Edit rule since #635, so it is locked
    /// with the rest rather than being a special case.
    /// </summary>
    [Fact]
    public async Task An_owner_may_not_modify_their_own_document_blocked_on_a_validation_warning()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        own.ReplaceFieldValidationWarnings(
            [new FieldValidationWarning(Guid.NewGuid(), "value does not match the source")]);
        own.ReviewReasons.HasFlag(DocumentReviewReasons.FieldValidationWarning).ShouldBeTrue();
        ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.FieldValidationWarning).ShouldBeTrue();

        await FailParseAsync(own);
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.UpdateMarkdownAsync(
                own.Id, new UpdateMarkdownInput { Markdown = "# corrected", Reprocess = true })));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.UpdateMarkdownAsync(
                own.Id, new UpdateMarkdownInput { Markdown = "# corrected", Reprocess = false })));
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.ReextractFieldsAsync(own.Id)));
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.RerecognizeAsync(own.Id)));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.UpdateCabinetAsync(own.Id, new UpdateDocumentCabinetInput { CabinetId = null })));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.RetryPipelineAsync(
                own.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse })));

        own.Markdown.ShouldBe("# body");
    }

    /// <summary>
    /// The lock is on <b>modification</b> only. Seeing and withdrawing one's own upload are
    /// <see cref="DocumentOwnerArm.Always"/> — neither can clear a review reason, and an uploader who could not
    /// even look at a document held for review would have no way to understand why.
    /// </summary>
    [Fact]
    public async Task An_owner_still_reads_and_deletes_their_own_document_under_review()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.ReplaceFieldValidationWarnings(
            [new FieldValidationWarning(Guid.NewGuid(), "value does not match the source")]);
        GrantUploaderOnTypeA();

        (await AsOwnerAsync(() => AppService.GetAsync(own.Id))).Id.ShouldBe(own.Id);

        await AsOwnerAsync(() => AppService.DeleteAsync(own.Id));
        await DocumentRepository.Received(1).DeleteAsync(own.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A suspected duplicate is the adversarial case the review queue exists for, so it locks the owner out of
    /// reclassifying — including to the <b>same</b> type, which is not refused as a no-op and which
    /// <c>Document.ConfirmClassification</c> would use to reset the duplicate state and clear the warnings.
    /// </summary>
    [Fact]
    public async Task An_owner_may_not_reclassify_their_own_suspected_duplicate_even_to_the_same_type()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        own.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id })));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.ConfirmClassificationAsync(
                own.Id, new ConfirmClassificationInput { DocumentTypeId = TypeA.Id })));

        own.ReviewReasons.HasFlag(DocumentReviewReasons.DuplicateSuspected).ShouldBeTrue();
    }

    /// <summary>
    /// The one blocking reason that does <b>not</b> lock the owner: classification. Confirming one's own upload
    /// is exactly what an uploader is expected to do, and the target type is judged separately by the DeclareType
    /// rule, which has no ownership arm at all.
    /// </summary>
    [Fact]
    public async Task An_owner_still_classifies_their_own_document_blocked_only_on_classification()
    {
        var own = StubDocument(documentTypeId: null, creatorId: OwnerId, markdown: "# body");
        own.SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: true);
        ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.UnresolvedClassification).ShouldBeTrue();

        GrantUploaderOnTypeA();

        var confirmed = await AsOwnerAsync(() => AppService.ConfirmClassificationAsync(
            own.Id, new ConfirmClassificationInput { DocumentTypeId = TypeA.Id }));

        confirmed.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    // ===================== Other people's documents =====================

    /// <summary>
    /// The complement of the whole decision: ownership adds rights to <b>one's own</b> documents and nothing
    /// else. The same uploader is refused every operation on somebody else's type-A document.
    /// </summary>
    [Fact]
    public async Task An_uploader_is_refused_every_operation_on_someone_elses_document_of_the_same_type()
    {
        var theirs = StubDocument(TypeA.Id, creatorId: StrangerId, markdown: "# body");
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() => AppService.GetAsync(theirs.Id)));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.UpdateMarkdownAsync(theirs.Id, new UpdateMarkdownInput { Markdown = "# no" })));
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.DeleteAsync(theirs.Id)));
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() => AppService.RetryPipelineAsync(
            theirs.Id, new RetryPipelineInput { PipelineCode = VaultExtractPipelines.Parse })));
    }

    /// <summary>
    /// And a <c>Read</c> grant opens exactly the read. It is the grant that extends rights to other people's
    /// documents of the type — one right at a time, not the family.
    /// </summary>
    [Fact]
    public async Task A_Read_grant_opens_the_read_of_someone_elses_document_and_nothing_more()
    {
        var theirs = StubDocument(TypeA.Id, creatorId: StrangerId);
        GrantUploaderOnTypeA();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id);

        (await AsOwnerAsync(() => AppService.GetAsync(theirs.Id))).Id.ShouldBe(theirs.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.UpdateMarkdownAsync(theirs.Id, new UpdateMarkdownInput { Markdown = "# no" })));
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.DeleteAsync(theirs.Id)));
    }

    // ===================== Untyped documents =====================

    /// <summary>
    /// The second failure #635 fixes: a document whose classification failed has no type, so no grant can ever
    /// name it — and before this, the documents that most need a human were exactly the ones their uploader could
    /// not reach. Ownership is the only arm that reaches an untyped row short of a module-wide permission.
    /// </summary>
    [Fact]
    public async Task An_untyped_document_is_visible_and_editable_to_its_uploader()
    {
        var own = StubDocument(documentTypeId: null, creatorId: OwnerId, markdown: "# body");
        StubQueryable(own);
        GrantUploaderOnTypeA();

        (await AsOwnerAsync(() => AppService.GetAsync(own.Id))).Id.ShouldBe(own.Id);

        var page = await AsOwnerAsync(() => AppService.GetListAsync(new GetDocumentListInput()));
        page.TotalCount.ShouldBe(1);

        // Classifying it is Edit on the current (absent) type — ownership — plus DeclareType on the target.
        var confirmed = await AsOwnerAsync(() => AppService.ConfirmClassificationAsync(
            own.Id, new ConfirmClassificationInput { DocumentTypeId = TypeA.Id }));
        confirmed.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    [Fact]
    public async Task An_untyped_document_is_invisible_to_a_caller_who_neither_owns_it_nor_holds_a_module_wide_permission()
    {
        var theirs = StubDocument(documentTypeId: null, creatorId: OwnerId);
        StubQueryable(theirs);
        // Every grant there is, on both types — none of which can name an untyped row.
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);
        GrantResource(VaultExtractResourcePermissions.Read, TypeB.Id, StrangerId);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => AppService.GetAsync(theirs.Id)));

        var page = await AsStrangerAsync(() => AppService.GetListAsync(new GetDocumentListInput()));
        page.TotalCount.ShouldBe(0);
    }

    // ===================== GetListAsync's type-code short circuit =====================

    /// <summary>
    /// #635 decision 3: a requested <c>DocumentTypeCode</c> this caller can produce no row of returns an empty
    /// page <b>before</b> the field filters are resolved, so an unknown-field error can no longer be used to
    /// enumerate a type's schema from outside the caller's scope. Empty page, not 403.
    /// </summary>
    [Fact]
    public async Task A_type_code_outside_the_scope_returns_an_empty_page_without_the_unknown_field_error()
    {
        StubQueryable(NewDocument(TypeB.Id, creatorId: StrangerId));
        // Entry plus a Read grant on A only: nothing of type B is reachable, and there is no owner arm to reach
        // one through because this principal owns nothing.
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        var page = await AsStrangerAsync(() => AppService.GetListAsync(new GetDocumentListInput
        {
            DocumentTypeCode = TypeB.TypeCode,
            FieldFilters = [new DocumentFieldFilter { Name = "invoice_no", Value = "42" }]
        }));

        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// The counter-case, so the short circuit cannot be "always return empty": a type the caller <i>can</i> reach
    /// still resolves its field filters, and an unknown field name still loud-fails as a correctable signal.
    /// </summary>
    [Fact]
    public async Task A_type_code_inside_the_scope_still_loud_fails_on_an_unknown_field()
    {
        StubQueryable(NewDocument(TypeA.Id, creatorId: StrangerId));
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        var exception = await Should.ThrowAsync<BusinessException>(() => AsStrangerAsync(
            () => AppService.GetListAsync(new GetDocumentListInput
            {
                DocumentTypeCode = TypeA.TypeCode,
                FieldFilters = [new DocumentFieldFilter { Name = "invoice_no", Value = "42" }]
            })));

        exception.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);
    }

    /// <summary>
    /// And an owner-armed caller is <b>not</b> short-circuited on a type they hold no grant on: they may own rows
    /// of it. Testing the short circuit with <c>Allows(subject{typeId, creator: null})</c> instead of
    /// "could this scope produce any row of this type" would have hidden an uploader's own documents the moment
    /// they filtered the list by their own type.
    /// </summary>
    [Fact]
    public async Task An_uploader_filtering_by_a_type_they_hold_no_grant_on_still_sees_their_own_rows()
    {
        var own = NewDocument(TypeB.Id, creatorId: OwnerId);
        StubQueryable(own, NewDocument(TypeB.Id, creatorId: StrangerId));
        GrantUploaderOnTypeA();

        var page = await AsOwnerAsync(() => AppService.GetListAsync(
            new GetDocumentListInput { DocumentTypeCode = TypeB.TypeCode }));

        page.TotalCount.ShouldBe(1);
        page.Items[0].Id.ShouldBe(own.Id);
    }

    // ===================== helpers =====================

    /// <summary>Puts a Failed Parse run on the document, so <c>RetryPipelineAsync</c> has something to retry.</summary>
    private async Task FailParseAsync(Document document)
    {
        var manager = GetRequiredService<DocumentPipelineRunManager>();
        var run = await manager.StartAsync(document, VaultExtractPipelines.Parse);
        await manager.FailAsync(document, run, errorMessage: "parse failed");
    }
}
