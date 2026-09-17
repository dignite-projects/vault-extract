using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 5: <b>rights are decided on the server and sent down, not re-derived in the browser.</b>
/// <para>
/// The client's copy of the rule keyed types by <c>TypeCode</c> against the active types while the server keys by
/// <c>Id</c> across soft-deleted ones — so a document on an archived type came back with every action missing —
/// and it had no way at all to express ownership, which is not a property of a type. These facts pin that the
/// answer on the row is the answer the endpoint enforces, row by row and including the archived-type case.
/// </para>
/// </summary>
public class DocumentRights_Tests : DocumentAccessTestBase
{
    /// <summary>
    /// The uploader's own document: everything except review. <c>CanReview</c> is the one that must be false, and
    /// it is the one a client-side re-derivation from "the caller's Edit grants" could not get right, because
    /// Review and Edit carry identical permission names.
    /// </summary>
    [Fact]
    public async Task The_detail_of_ones_own_document_carries_every_right_except_review()
    {
        var own = StubDocument(TypeA.Id, creatorId: OwnerId);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);
        GrantResource(VaultExtractResourcePermissions.Upload, TypeA.Id);

        var rights = (await AsOwnerAsync(() => AppService.GetAsync(own.Id))).Rights;

        rights.CanRead.ShouldBeTrue();
        rights.CanEdit.ShouldBeTrue();
        rights.CanDelete.ShouldBeTrue();
        rights.CanRestore.ShouldBeTrue();
        rights.CanRetry.ShouldBeTrue();
        rights.CanReview.ShouldBeFalse();
    }

    [Fact]
    public async Task A_Read_grant_on_someone_elses_document_carries_only_CanRead()
    {
        var theirs = StubDocument(TypeA.Id, creatorId: StrangerId);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id);

        var rights = (await AsOwnerAsync(() => AppService.GetAsync(theirs.Id))).Rights;

        rights.CanRead.ShouldBeTrue();
        rights.CanEdit.ShouldBeFalse();
        rights.CanReview.ShouldBeFalse();
        rights.CanDelete.ShouldBeFalse();
        rights.CanRestore.ShouldBeFalse();
        rights.CanRetry.ShouldBeFalse();
    }

    /// <summary>
    /// The list decides per row, not per page: the caller's own row and a stranger's row of the <b>same type</b>
    /// carry different rights.
    /// </summary>
    [Fact]
    public async Task List_rows_carry_their_own_rights_even_within_one_document_type()
    {
        var own = NewDocument(TypeA.Id, creatorId: OwnerId);
        var theirs = NewDocument(TypeA.Id, creatorId: StrangerId);
        StubQueryable(own, theirs);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);
        GrantResource(VaultExtractResourcePermissions.Upload, TypeA.Id);
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id);

        var page = await AsOwnerAsync(() => AppService.GetListAsync(new GetDocumentListInput()));

        page.TotalCount.ShouldBe(2);
        var mine = page.Items.Single(i => i.Id == own.Id);
        var theirRow = page.Items.Single(i => i.Id == theirs.Id);

        mine.Rights.CanEdit.ShouldBeTrue();
        mine.Rights.CanDelete.ShouldBeTrue();
        theirRow.Rights.CanRead.ShouldBeTrue();
        theirRow.Rights.CanEdit.ShouldBeFalse();
        theirRow.Rights.CanDelete.ShouldBeFalse();
    }

    /// <summary>
    /// The archived-type divergence the client-side table caused: the server keys grants by the type's immutable
    /// <c>Id</c> and the grant map sweeps the layer across soft delete, so a document on a since-archived type
    /// keeps exactly the rights the endpoints still admit.
    /// </summary>
    [Fact]
    public async Task A_document_on_an_archived_type_keeps_the_rights_the_endpoints_admit()
    {
        var document = StubDocument(TypeA.Id, creatorId: StrangerId);
        TypeA.IsDeleted = true;
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id);
        GrantResource(VaultExtractResourcePermissions.Edit, TypeA.Id);

        var rights = (await AsOwnerAsync(() => AppService.GetAsync(document.Id))).Rights;

        rights.CanEdit.ShouldBeTrue();
        rights.CanReview.ShouldBeTrue();

        // And the endpoint agrees, which is the whole point of sending the answer rather than the inputs.
        (await AsOwnerAsync(() =>
            AppService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" }))).Id
            .ShouldBe(document.Id);
    }

    /// <summary>
    /// A caller who reaches a document only through ownership still gets <c>CanRead</c> on it — the answer the
    /// client could not have produced, because there is no grant anywhere to derive it from.
    /// </summary>
    [Fact]
    public async Task An_untyped_own_document_carries_its_owners_rights()
    {
        var own = StubDocument(documentTypeId: null, creatorId: OwnerId);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        var rights = (await AsOwnerAsync(() => AppService.GetAsync(own.Id))).Rights;

        rights.CanRead.ShouldBeTrue();
        rights.CanEdit.ShouldBeTrue();
        rights.CanDelete.ShouldBeTrue();
        rights.CanReview.ShouldBeFalse();
    }

    /// <summary>
    /// #635 decision 5 and 11, stated structurally: no <c>CreatorId</c> on either egress DTO. The client needs to
    /// know what it may do with a document, never who owns it, and adding the uploader to every list row would put
    /// a personal-data field on the wire to answer a question the six booleans already answer.
    /// </summary>
    [Fact]
    public void Neither_egress_DTO_exposes_the_uploader()
    {
        typeof(DocumentDto).GetProperty("CreatorId").ShouldBeNull();
        typeof(DocumentListItemDto).GetProperty("CreatorId").ShouldBeNull();
        typeof(DocumentRightsDto).GetProperty("CreatorId").ShouldBeNull();
    }
}
