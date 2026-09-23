using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Documents.Review;
using Dignite.Vault.Extract.Permissions;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Content;
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
/// <para>
/// #645 made that shape exactly the ordinary uploader: it no longer carries <c>Documents.Upload</c>, which since
/// #645 means "upload into <b>all</b> document types" and would also let this principal move its documents into
/// any type — the opposite of what a per-type uploader is.
/// </para>
/// </summary>
public class DocumentOwnership_Tests : DocumentAccessTestBase
{
    /// <summary>The Issue's day-one principal, in one place so every fact starts from the same grants.</summary>
    private void GrantUploaderOnTypeA()
    {
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Upload, TypeA.Id);
    }

    // ===================== Uploading: entry + a type-level Upload grant is the whole uploader =====================

    /// <summary>
    /// #645 decision 1: configuring an ordinary uploader is entry + an <c>Upload</c> grant on each type they may
    /// upload into. That principal uploads into A — no <c>Documents.Upload</c> — is refused B, and is refused an
    /// untyped upload, which only the role-level <c>Documents.Upload</c> admits.
    /// </summary>
    [Fact]
    public async Task Entry_and_an_Upload_grant_on_A_upload_into_A_and_nowhere_else()
    {
        GrantUploaderOnTypeA();

        var created = await AsOwnerAsync(() => AppService.UploadAsync(NewUpload("a.txt", TypeA.Id)));
        created.Id.ShouldNotBe(Guid.Empty);
        await DocumentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d => d.DocumentTypeId == TypeA.Id), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.UploadAsync(NewUpload("b.txt", TypeB.Id))));
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.UploadAsync(NewUpload("untyped.txt", documentTypeId: null))));

        await DocumentRepository.Received(1).InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ===================== A reclassification's target type (DeclareType) =====================

    /// <summary>
    /// #645 acceptance: the target type of a reclassification admits <c>ConfirmClassification</c>,
    /// <c>Documents.Upload</c>, or an <c>Upload</c> grant on the target — and refuses a caller holding none of them.
    /// Every fact below reclassifies the caller's own type-B document, so the <b>current</b>-type half of the rule
    /// (Edit) is answered by ownership each time and the outcome is about the target alone.
    /// </summary>
    [Fact]
    public async Task A_reclassification_target_is_refused_without_ConfirmClassification_Documents_Upload_or_a_grant()
    {
        var own = StubDocument(TypeB.Id, creatorId: OwnerId, markdown: "# body");
        GrantEntryOnly();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id })));

        own.DocumentTypeId.ShouldBe(TypeB.Id);
    }

    [Fact]
    public async Task A_reclassification_target_admits_ConfirmClassification()
    {
        var own = StubDocument(TypeB.Id, creatorId: OwnerId, markdown: "# body");
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ConfirmClassification);

        var reclassified = await AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id }));

        reclassified.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    /// <summary>
    /// The member #645 added to DeclareType's role-level set: someone who could have uploaded into any type may
    /// also move their document into any type. The caller holds no grant on A and no
    /// <c>ConfirmClassification</c>, so <c>Documents.Upload</c> is the only thing that can admit the target.
    /// </summary>
    [Fact]
    public async Task A_reclassification_target_admits_Documents_Upload()
    {
        var own = StubDocument(TypeB.Id, creatorId: OwnerId, markdown: "# body");
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        var reclassified = await AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id }));

        reclassified.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    [Fact]
    public async Task A_reclassification_target_admits_an_Upload_grant_on_the_target()
    {
        var own = StubDocument(TypeB.Id, creatorId: OwnerId, markdown: "# body");
        GrantUploaderOnTypeA();

        var reclassified = await AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id }));

        reclassified.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    // ===================== Re-parse re-classifies, so it asks DeclareType too (#648 / #660) =====================

    /// <summary>
    /// #648: the classifier picks the target and may land the document in any type of the layer, so re-parse
    /// (<c>ReparseAsync</c>, #660, which re-runs classification) judges <see cref="DocumentAccessRule.DeclareType"/> on the empty subject — leaving
    /// its role-level arm only, exactly as an untyped upload is judged. A per-type uploader keeps the manual
    /// path, which names a type their grant covers.
    /// </summary>
    [Fact]
    public async Task An_uploader_is_refused_reparse_of_their_own_document_but_still_reclassifies_it()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.ReparseAsync(own.Id)));

        var reclassified = await AsOwnerAsync(() =>
            AppService.ReclassifyAsync(own.Id, new ReclassifyDocumentInput { DocumentTypeId = TypeA.Id }));

        reclassified.DocumentTypeCode.ShouldBe(TypeA.TypeCode);
    }

    /// <summary>
    /// The other half of #648's report: an <c>Edit</c> grant admits editing a type's documents, not pushing them
    /// out of the type the grant was given on.
    /// </summary>
    [Fact]
    public async Task An_Edit_grant_alone_does_not_admit_reparse_of_someone_elses_document()
    {
        var theirs = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id, StrangerId);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => AppService.ReparseAsync(theirs.Id)));

        (await LatestParseRunAsync(theirs)).ShouldBeNull();
    }

    [Fact]
    public async Task Reparse_admits_ConfirmClassification_on_someone_elses_document()
    {
        BlobContainer.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var theirs = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ConfirmClassification);

        await AsStrangerAsync(() => AppService.ReparseAsync(theirs.Id));

        (await LatestParseRunAsync(theirs))!.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    /// <summary>
    /// <c>Documents.Upload</c> is not on the Edit rule, so ownership answers that half and this fact is about the
    /// DeclareType half alone.
    /// </summary>
    [Fact]
    public async Task Reparse_admits_Documents_Upload_on_ones_own_document()
    {
        BlobContainer.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var own = StubDocument(TypeA.Id, creatorId: OwnerId, markdown: "# body");
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        await AsOwnerAsync(() => AppService.ReparseAsync(own.Id));

        (await LatestParseRunAsync(own))!.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    /// <summary>
    /// Authorization outranks the fast-fail, the same ordering Confirm / Reclassify already carry: a business
    /// error is an oracle for this document's processing state.
    /// </summary>
    [Fact]
    public async Task Reparse_denies_before_the_NotTextExtracted_guard_can_answer()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.Markdown.ShouldBeNullOrEmpty();
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.ReparseAsync(own.Id)));
    }

    private Task<DocumentPipelineRun?> LatestParseRunAsync(Document document)
        => GetRequiredService<IDocumentPipelineRunRepository>()
            .FindLatestByDocumentAndCodeAsync(document.Id, VaultExtractPipelines.Parse);

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

    /// <summary>
    /// #645 acceptance: a caller holding neither <c>Documents.Delete</c> nor a <c>Delete</c> grant restores only
    /// their own documents — ownership is the one arm left, and it reaches nobody else's, of the same type or
    /// untyped. (A role holding the removed role-level restore permission without <c>Documents.Delete</c> lands
    /// exactly here after the upgrade.)
    /// </summary>
    [Fact]
    public async Task Without_Documents_Delete_or_a_Delete_grant_a_caller_restores_only_their_own_documents()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId, deleted: true);
        var theirs = StubDocument(TypeA.Id, creatorId: StrangerId, deleted: true);
        var theirsUntyped = StubDocument(documentTypeId: null, creatorId: StrangerId, deleted: true);
        GrantUploaderOnTypeA();

        await AsOwnerAsync(() => AppService.RestoreAsync(own.Id));
        own.IsDeleted.ShouldBeFalse();

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsOwnerAsync(() => AppService.RestoreAsync(theirs.Id)));
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.RestoreAsync(theirsUntyped.Id)));
        theirs.IsDeleted.ShouldBeTrue();
        theirsUntyped.IsDeleted.ShouldBeTrue();
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
    /// The review invariant, held where it can actually hold. <b>#657 note</b>: before #657 the edit family cleared
    /// <see cref="DocumentReviewReasons.FieldExtractionIncomplete"/> as a side effect of any edit (even an empty
    /// one), which was the whole reason this lock had to exist for that reason too. Since #657
    /// <c>UpdateExtractedFieldsAsync</c> no longer touches that bit at all — only <c>ConfirmFieldEntryAsync</c>
    /// does, gated by the separate <see cref="DocumentAccessRule.Review"/> rule below — but the lock below still
    /// matters on its own terms: an uploader must not even be able to attempt an edit-family call on their own
    /// document while it carries <b>any</b> blocking reason other than classification (e.g. a corrected
    /// unique-key value quietly changing <see cref="DocumentReviewReasons.DuplicateSuspected"/>), independent of
    /// what that particular call would or would not clear.
    /// <para>
    /// So while a document carries a blocking reason other than classification, the ownership arm of the edit
    /// family is shut (<see cref="DocumentOwnerArm.UnlessUnderReview"/>).
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

    /// <summary>
    /// #657: <c>ConfirmFieldEntryAsync</c> is the only call that clears <c>FieldExtractionIncomplete</c>, and it
    /// runs on <see cref="DocumentAccessRule.Review"/>, not Edit — so the document's own uploader is refused it,
    /// exactly like <c>AllowDuplicateAsync</c> / <c>ResolveFieldValidationWarningsAsync</c>.
    /// </summary>
    [Fact]
    public async Task An_uploader_is_refused_ConfirmFieldEntryAsync_on_their_own_document()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        GrantUploaderOnTypeA();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsOwnerAsync(() => AppService.ConfirmFieldEntryAsync(own.Id)));

        own.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeTrue();
    }

    [Fact]
    public async Task An_Edit_grant_on_the_type_confirms_field_entry_on_a_document_blocked_on_incomplete_extraction()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        GrantUploaderOnTypeA();
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id);

        await AsOwnerAsync(() => AppService.ConfirmFieldEntryAsync(own.Id));

        // #657's escape path: an Edit grant is also one of the Review rule's role-level members (as it is for
        // AllowDuplicateAsync / ResolveFieldValidationWarningsAsync), so it admits confirmation too.
        own.ReviewReasons.HasFlag(DocumentReviewReasons.FieldExtractionIncomplete).ShouldBeFalse();
    }

    [Fact]
    public async Task The_module_wide_ConfirmClassification_confirms_field_entry_on_a_document_under_review()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        own.SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: true);
        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.ConfirmClassification);

        await AsOwnerAsync(() => AppService.ConfirmFieldEntryAsync(own.Id));

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
            () => AsOwnerAsync(() => AppService.ReparseAsync(own.Id)));
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
    /// A requested <c>DocumentTypeCode</c> this caller can produce no row of returns an empty page — not a 403,
    /// because after #635 an owner legitimately lists a type they hold no grant on and gets their own rows back.
    /// The rows are narrowed by the scope predicate, so "outside the scope" needs no branch of its own.
    /// <para>
    /// #635 first added a scope-dependent short circuit here so an unknown-field error could not describe a
    /// type's schema to such a caller. The code review established that it reduced no disclosure —
    /// <c>IFieldDefinitionAppService.GetListAsync</c> hands the whole layer's field definitions to any entry
    /// holder by recorded decision (#223 / #629) — so it is gone, and the next fact is the one that matters.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_type_code_outside_the_scope_returns_an_empty_page()
    {
        // Somebody ELSE`s type-B document: with StrangerId as the creator the caller`s own ownership arm would
        // reach it, and this fact would pass for the wrong reason.
        StubQueryable(NewDocument(TypeB.Id, creatorId: OwnerId));
        // Entry plus a Read grant on A only: nothing of type B is reachable, and there is no owner arm to reach
        // one through because this principal owns nothing.
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        var page = await AsStrangerAsync(() => AppService.GetListAsync(
            new GetDocumentListInput { DocumentTypeCode = TypeB.TypeCode }));

        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// An unknown field name loud-fails for <b>everyone</b>, scope or no scope: it is a correctable signal, and
    /// swallowing it for some callers cost an uploader the message on their own documents while disclosing
    /// nothing that four other surfaces do not already disclose.
    /// </summary>
    [Fact]
    public async Task An_unknown_field_loud_fails_for_a_caller_outside_the_types_scope_too()
    {
        StubQueryable(NewDocument(TypeB.Id, creatorId: OwnerId));
        Grant(VaultExtractPermissions.Documents.Default);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        var exception = await Should.ThrowAsync<BusinessException>(() => AsStrangerAsync(
            () => AppService.GetListAsync(new GetDocumentListInput
            {
                DocumentTypeCode = TypeB.TypeCode,
                FieldFilters = [new DocumentFieldFilter { Name = "invoice_no", Value = "42" }]
            })));

        exception.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);
    }

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

    // ===================== Export narrows by the same scope, ownership included =====================

    /// <summary>
    /// Acceptance: "GetListAsync, ExportAsync and MCP search_documents return the caller's own documents in
    /// addition to the types they may read." The list is covered above and MCP search calls
    /// <c>GetListAsync</c>, but <see cref="Documents.Exports.DocumentExportAppService"/> composes its own query
    /// and resolves the <see cref="DocumentAccessRule.Read"/> scope separately from its own admission rule
    /// (<see cref="DocumentAccessRule.Export"/>, which has no owner arm at all -- it only gates the call itself).
    /// This pins that the ROWS the export narrows down to still include what the caller owns.
    /// </summary>
    [Fact]
    public async Task An_uploader_without_a_Read_grant_still_exports_their_own_document_and_not_a_strangers()
    {
        var own = NewDocument(TypeA.Id, creatorId: OwnerId);
        var theirs = NewDocument(TypeA.Id, creatorId: StrangerId);
        StubQueryable(own, theirs);

        // Entry + the module-wide Export admission only: no Read grant anywhere, no ReadAll. Export's own rule
        // has OwnerArm.Never, so this grant set is what gets the CALL admitted; the rows are narrowed below.
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Export);

        var exportAppService = GetRequiredService<IDocumentExportAppService>();
        var file = await AsOwnerAsync(() => exportAppService.ExportAsync(
            new ExportDocumentsInput { DocumentTypeCode = TypeA.TypeCode, Format = ExportFormat.Csv }));

        (await CountDataRowsAsync(file)).ShouldBe(1);
    }

    [Fact]
    public async Task A_ReadAll_holder_exports_every_document_of_the_type_including_a_strangers()
    {
        var own = NewDocument(TypeA.Id, creatorId: OwnerId);
        var theirs = NewDocument(TypeA.Id, creatorId: StrangerId);
        StubQueryable(own, theirs);

        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Export,
            VaultExtractPermissions.Documents.ReadAll);

        var exportAppService = GetRequiredService<IDocumentExportAppService>();
        var file = await AsOwnerAsync(() => exportAppService.ExportAsync(
            new ExportDocumentsInput { DocumentTypeCode = TypeA.TypeCode, Format = ExportFormat.Csv }));

        (await CountDataRowsAsync(file)).ShouldBe(2);
    }

    /// <summary>Counts CSV data rows (header excluded) -- the least brittle way to tell "own only" from "both".</summary>
    private static async Task<int> CountDataRowsAsync(IRemoteStreamContent file)
    {
        using var reader = new StreamReader(file.GetStream());
        var text = await reader.ReadToEndAsync();
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length - 1;
    }

    // ===================== helpers =====================

    /// <summary>Puts a Failed Parse run on the document, so <c>RetryPipelineAsync</c> has something to retry.</summary>
    private async Task FailParseAsync(Document document)
    {
        var manager = GetRequiredService<DocumentPipelineRunManager>();
        var run = await manager.StartAsync(document, VaultExtractPipelines.Parse);
        await manager.FailAsync(document, run, errorMessage: "parse failed");
    }

    /// <summary>A small text upload whose body is its file name, so two uploads never share a content hash.</summary>
    private static UploadDocumentInput NewUpload(string fileName, Guid? documentTypeId)
        => new()
        {
            File = new RemoteStreamContent(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileName)), fileName, "text/plain"),
            DocumentTypeId = documentTypeId
        };
}
