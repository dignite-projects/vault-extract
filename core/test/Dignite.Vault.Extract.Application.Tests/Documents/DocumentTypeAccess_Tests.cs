using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentTypeAccessTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The module-wide half of every rule is decided by a controllable grant set; the PER-TYPE half is
        // deliberately NOT faked — GrantSetAuthorizationService leaves it to ABP's real ResourcePermissionChecker
        // and its real user / role value providers, reading InMemoryResourcePermissionStore in place of the
        // AbpResourcePermissionGrants table (#629's seam, reused). Stubbing the per-type half would have let the
        // OR assert itself.
        context.Services.AddSingleton(sp => new GrantSetAuthorizationService(sp));
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAbpAuthorizationService>();
        context.Services.AddSingleton<IAuthorizationService>(sp => sp.GetRequiredService<GrantSetAuthorizationService>());
        context.Services.AddSingleton<IAbpAuthorizationService>(sp => sp.GetRequiredService<GrantSetAuthorizationService>());

        context.Services.AddSingleton<InMemoryResourcePermissionStore>();
        context.Services.RemoveAll<IResourcePermissionStore>();
        context.Services.AddSingleton<IResourcePermissionStore>(sp => sp.GetRequiredService<InMemoryResourcePermissionStore>());

        // The scoped grant memo, plus one extra reason to forget: this host changes what a principal is granted
        // inside one scope, which no request does. See TestDocumentAccessMemo.
        context.Services.UseTestAccessMemo();

        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #632: the per-document-type Read / Edit / Delete matrix and the list / export read scope, at the application
/// layer.
/// <para>
/// Every fact runs as a principal holding only <c>Documents.Default</c> — <b>entry</b>, which since #632 no longer
/// means "read everything" — plus one resource grant on type A. The module-wide comparison cases hold
/// <c>Documents.ReadAll</c> / <c>ConfirmClassification</c> / <c>Documents.Delete</c> instead, and must behave
/// exactly as they did before this change.
/// </para>
/// <para>
/// Untyped documents get their own facts in each family: they belong to no type, so no grant can name them and
/// only the module-wide permission reaches them. That is the fail-closed half of the rule, and the half a
/// mistakenly permissive default would silently pass.
/// </para>
/// </summary>
public class DocumentTypeAccess_Tests : VaultExtractApplicationTestBase<DocumentTypeAccessTestModule>
{
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IDocumentAppService _appService;
    private readonly IDocumentExportAppService _exportAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly GrantSetAuthorizationService _authorization;
    private readonly InMemoryResourcePermissionStore _resourcePermissionStore;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    private readonly DocumentType _typeA = new(Guid.NewGuid(), null, "access.a", "Type A");
    private readonly DocumentType _typeB = new(Guid.NewGuid(), null, "access.b", "Type B");

    public DocumentTypeAccess_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _exportAppService = GetRequiredService<IDocumentExportAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _authorization = GetRequiredService<GrantSetAuthorizationService>();
        _resourcePermissionStore = GetRequiredService<InMemoryResourcePermissionStore>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();

        // The layer's own type list: what the read-scope sweep enumerates, and what the DTO reference maps resolve.
        _documentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([_typeA, _typeB]);
        _documentTypeRepository.GetListAsync(
                Arg.Any<Expression<Func<DocumentType, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<DocumentType, bool>>>().Compile();
                return new List<DocumentType>(new[] { _typeA, _typeB }.Where(predicate));
            });
        _documentTypeRepository.FindByTypeCodeAsync(_typeA.TypeCode, Arg.Any<CancellationToken>()).Returns(_typeA);
        _documentTypeRepository.FindByTypeCodeAsync(_typeB.TypeCode, Arg.Any<CancellationToken>()).Returns(_typeB);
        _documentTypeRepository.FindAsync(_typeA.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_typeA);
        _documentTypeRepository.FindAsync(_typeB.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_typeB);

        // No field definitions anywhere: these facts are about authorization, not about field assembly.
        _fieldRepository.GetListAsync(
                Arg.Any<Expression<Func<Field, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _fieldRepository.GetListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    // ===================== Read =====================

    [Fact]
    public async Task GetAsync_is_admitted_by_a_Read_grant_on_the_documents_own_type()
    {
        var document = StubDocument(_typeA.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var dto = await AsPrincipalAsync(() => _appService.GetAsync(document.Id));

        dto.Id.ShouldBe(document.Id);
        dto.DocumentTypeCode.ShouldBe(_typeA.TypeCode);
    }

    [Fact]
    public async Task GetAsync_is_denied_for_a_document_of_another_type()
    {
        var document = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.GetAsync(document.Id)));
    }

    /// <summary>
    /// The fail-closed half: an untyped document (unclassified / failed classification / container) belongs to no
    /// type, so the grant on A cannot reach it and only <c>Documents.ReadAll</c> can.
    /// </summary>
    [Fact]
    public async Task GetAsync_is_denied_for_an_untyped_document_however_many_grants_the_caller_holds()
    {
        var document = StubDocument(documentTypeId: null);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);
        GrantResource(VaultExtractResourcePermissions.Read, _typeB.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.GetAsync(document.Id)));
    }

    [Fact]
    public async Task GetAsync_is_unchanged_for_a_ReadAll_holder_including_untyped_documents()
    {
        var typed = StubDocument(_typeB.Id);
        var untyped = StubDocument(documentTypeId: null);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        (await AsPrincipalAsync(() => _appService.GetAsync(typed.Id))).Id.ShouldBe(typed.Id);
        (await AsPrincipalAsync(() => _appService.GetAsync(untyped.Id))).Id.ShouldBe(untyped.Id);
    }

    [Fact]
    public async Task GetBlobAsync_follows_the_same_Read_rule_as_GetAsync()
    {
        var granted = StubDocument(_typeA.Id);
        var denied = StubDocument(_typeB.Id);
        _blobContainer.GetAsync(granted.FileOrigin!.BlobName, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream([1, 2, 3]));

        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var stream = await AsPrincipalAsync(() => _appService.GetBlobAsync(granted.Id));
        stream.ShouldNotBeNull();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.GetBlobAsync(denied.Id)));
    }

    // ===================== FindForCallerAsync (#636) =====================

    [Fact]
    public async Task FindForCallerAsync_returns_the_dto_when_granted()
    {
        var document = StubDocument(_typeA.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var dto = await AsPrincipalAsync(() => _appService.FindForCallerAsync(document.Id));

        dto.ShouldNotBeNull();
        dto!.Id.ShouldBe(document.Id);
        dto.DocumentTypeCode.ShouldBe(_typeA.TypeCode);
    }

    /// <summary>Mirrors <see cref="GetAsync_is_denied_for_a_document_of_another_type"/>: null instead of a throw.</summary>
    [Fact]
    public async Task FindForCallerAsync_returns_null_for_a_document_of_another_type()
    {
        var document = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var dto = await AsPrincipalAsync(() => _appService.FindForCallerAsync(document.Id));

        dto.ShouldBeNull();
    }

    /// <summary>
    /// Mirrors <see cref="GetAsync_is_denied_for_an_untyped_document_however_many_grants_the_caller_holds"/>: the
    /// fail-closed half — an untyped document belongs to no type, so no per-type grant can reach it.
    /// </summary>
    [Fact]
    public async Task FindForCallerAsync_returns_null_for_an_untyped_document_however_many_grants_the_caller_holds()
    {
        var document = StubDocument(documentTypeId: null);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);
        GrantResource(VaultExtractResourcePermissions.Read, _typeB.Id);

        var dto = await AsPrincipalAsync(() => _appService.FindForCallerAsync(document.Id));

        dto.ShouldBeNull();
    }

    /// <summary>Nonexistent id (unstubbed on the repository) — the third null case, alongside wrong-type and untyped.</summary>
    [Fact]
    public async Task FindForCallerAsync_returns_null_for_a_nonexistent_document()
    {
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var dto = await AsPrincipalAsync(() => _appService.FindForCallerAsync(Guid.NewGuid()));

        dto.ShouldBeNull();
    }

    /// <summary>
    /// The one case that does NOT fold to null: a caller with no entry (Documents.Default) at all still throws
    /// AbpAuthorizationException, unrelated to whether the id names a real document — entry is a caller-wide fact,
    /// not an id-specific one, and folding it into null would make "no permission" indistinguishable from "wrong id".
    /// </summary>
    [Fact]
    public async Task FindForCallerAsync_throws_when_the_caller_has_no_entry()
    {
        var document = StubDocument(_typeA.Id);
        GrantNothing();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.FindForCallerAsync(document.Id)));
    }

    // ===================== Edit =====================

    [Fact]
    public async Task An_edit_is_admitted_by_an_Edit_grant_on_the_documents_own_type_and_denied_on_another()
    {
        var granted = StubDocument(_typeA.Id);
        var denied = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Edit, _typeA.Id);

        // RejectReviewAsync stands in for the whole edit family: all nine methods share the one helper call, so a
        // per-method repeat would assert the same line nine times. The family membership itself is asserted
        // structurally by Every_edit_family_method_lost_its_module_wide_Authorize_attribute below.
        var dto = await AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(granted.Id, new RejectReviewInput { Reason = "not a contract" }));
        dto.Id.ShouldBe(granted.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(denied.Id, new RejectReviewInput { Reason = "not a contract" })));
    }

    [Fact]
    public async Task An_edit_on_an_untyped_document_needs_the_module_wide_permission()
    {
        var document = StubDocument(documentTypeId: null);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Edit, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" })));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ConfirmClassification);

        var dto = await AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" }));
        dto.Id.ShouldBe(document.Id);
    }

    /// <summary>
    /// #635: <b>no</b> method of the three documents-domain app services carries an <c>[Authorize]</c> attribute
    /// any more, and neither does the class. The documents domain has one way to declare authorization and it is
    /// a row in <see cref="DocumentAccessRule"/>'s table.
    /// <para>
    /// This is structural rather than behavioural on purpose. An attribute fires before the method body, so
    /// re-adding one would deny a per-type grant holder — or an owner — before the body could offer the other
    /// arms of the OR, breaking exactly one path and leaving every other fact in this file green. The previous
    /// version of this test allowed the module-wide-only operations to keep theirs, which is exactly how
    /// <c>PermanentDeleteAsync</c> / <c>RetryPipelineAsync</c> / the <c>Reprocessing</c> four / the export kept
    /// asserting no entry at all.
    /// </para>
    /// </summary>
    [Fact]
    public void No_method_of_the_documents_domain_app_services_carries_an_Authorize_attribute()
    {
        Type[] services =
        [
            typeof(DocumentAppService),
            typeof(DocumentExportAppService),
            typeof(Reprocessing.DocumentReprocessingAppService)
        ];

        foreach (var service in services)
        {
            service.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .ShouldBeEmpty($"{service.Name} must not carry a class-level [Authorize] (#635).");

            foreach (var method in service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).ShouldBeEmpty(
                    $"{service.Name}.{method.Name} must not carry [Authorize]: authorization is a rule-table row.");
            }
        }

        // Counter-case, so this cannot pass by the attribute type simply never being found anywhere: a service
        // outside the documents domain still declares its gate the ordinary way.
        typeof(DocumentTypes.DocumentTypeAppService).GetMethod(nameof(DocumentTypes.IDocumentTypeAppService.CreateAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).ShouldNotBeEmpty();
    }

    // ===================== Delete =====================

    [Fact]
    public async Task DeleteAsync_is_admitted_by_a_Delete_grant_on_the_documents_own_type_and_denied_on_another()
    {
        var granted = StubDocument(_typeA.Id);
        var denied = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await AsPrincipalAsync(() => _appService.DeleteAsync(granted.Id));
        await _documentRepository.Received(1).DeleteAsync(granted.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.DeleteAsync(denied.Id)));
        await _documentRepository.DidNotReceive().DeleteAsync(denied.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_is_unchanged_for_a_module_wide_Delete_holder()
    {
        var untyped = StubDocument(documentTypeId: null);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Delete);

        await AsPrincipalAsync(() => _appService.DeleteAsync(untyped.Id));

        await _documentRepository.Received(1).DeleteAsync(untyped.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ===================== Restore =====================

    /// <summary>
    /// #632 change 2, "whoever may delete may undo": the SAME <c>Delete</c> grant that admits
    /// <c>DeleteAsync</c> admits <c>RestoreAsync</c>. No <c>Documents.Restore</c> is held here, so the
    /// module-wide half of the OR contributes nothing.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_is_admitted_by_a_Delete_grant_on_the_documents_own_type_and_denied_on_another()
    {
        var granted = StubDocument(_typeA.Id, deleted: true);
        var denied = StubDocument(_typeB.Id, deleted: true);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await AsPrincipalAsync(() => _appService.RestoreAsync(granted.Id));
        granted.IsDeleted.ShouldBeFalse();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.RestoreAsync(denied.Id)));
        denied.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task RestoreAsync_is_unchanged_for_a_module_wide_Restore_holder_including_untyped_documents()
    {
        var untyped = StubDocument(documentTypeId: null, deleted: true);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        // Untyped: no grant can name it, so the per-type half cannot reach it.
        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.RestoreAsync(untyped.Id)));
        untyped.IsDeleted.ShouldBeTrue();

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Restore);

        await AsPrincipalAsync(() => _appService.RestoreAsync(untyped.Id));
        untyped.IsDeleted.ShouldBeFalse();
    }

    /// <summary>
    /// The check sits before the <c>!IsDeleted</c> early return on purpose. That return is observable — a silent
    /// success — so checking after it would let a caller with no right on this type learn whether a document is in
    /// the recycle bin from whether the call throws.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_denies_a_live_document_too_rather_than_returning_silently()
    {
        var live = StubDocument(_typeB.Id, deleted: false);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.RestoreAsync(live.Id)));
    }

    /// <summary>
    /// And before the two business guards, so their error messages cannot be used as an oracle: here the #531
    /// archived-type guard would answer <c>RestoreTypeDeleted</c> — a statement about the layer's schema — to a
    /// caller who may not restore this type at all.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_denies_before_a_business_guard_can_answer_anything()
    {
        var document = StubDocument(_typeB.Id, deleted: true);
        _typeB.IsDeleted = true;
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.RestoreAsync(document.Id)));
    }

    // ===================== Recycle-bin admission =====================

    /// <summary>
    /// #635 decision 6: admission to the recycle bin is entry and nothing else — asserted by resolving the Read
    /// scope, exactly as the ordinary list does. A Delete grant is no longer required to open it.
    /// </summary>
    [Fact]
    public async Task The_recycle_bin_admits_a_caller_holding_only_a_Delete_grant()
    {
        StubQueryable(
            NewDocument(_typeA.Id, deleted: true),
            NewDocument(_typeB.Id, deleted: true),
            NewDocument(_typeA.Id));
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var page = await AsPrincipalAsync(
            () => _appService.GetListAsync(new GetDocumentListInput { IsDeleted = true }));

        page.TotalCount.ShouldBe(1);
        page.Items.ShouldAllBe(i => i.DocumentTypeCode == _typeA.TypeCode);
    }

    /// <summary>
    /// #635 decision 6, the behaviour change: a caller who may READ type A but holds no Delete grant anywhere is
    /// now admitted and sees its deleted type-A documents, instead of being refused outright.
    /// <para>
    /// Under #632 the bin admitted by the Restore arm and narrowed by the Read arm — two different questions, so
    /// a Delete-grant-only caller was let into a bin the UI then reported as empty while it was not, and a
    /// Read-only caller could not look at their own layer's recycle bin at all. Rows are the Read scope's
    /// soft-deleted documents now; whether each one can actually be restored is the row's own
    /// <c>rights.canRestore</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_recycle_bin_admits_a_caller_who_may_read_but_not_delete_and_shows_their_readable_rows()
    {
        StubQueryable(NewDocument(_typeA.Id, deleted: true), NewDocument(_typeB.Id, deleted: true));
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var page = await AsPrincipalAsync(
            () => _appService.GetListAsync(new GetDocumentListInput { IsDeleted = true }));

        page.TotalCount.ShouldBe(1);
        page.Items.ShouldAllBe(i => i.DocumentTypeCode == _typeA.TypeCode);
    }

    /// <summary>
    /// The other half of the same change: a Delete grant grants nothing on the read side, so a caller who may
    /// delete type A but not read it still sees an empty bin. Fail-closed, and now genuinely empty by
    /// construction rather than as an admitted-then-narrowed special case.
    /// </summary>
    [Fact]
    public async Task The_recycle_bin_rows_stay_narrowed_by_the_read_scope_not_by_the_delete_grant()
    {
        StubQueryable(NewDocument(_typeA.Id, deleted: true), NewDocument(_typeB.Id, deleted: true));
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        var page = await AsPrincipalAsync(
            () => _appService.GetListAsync(new GetDocumentListInput { IsDeleted = true }));

        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_recycle_bin_is_unchanged_for_a_module_wide_Restore_holder()
    {
        StubQueryable(
            NewDocument(_typeA.Id, deleted: true), NewDocument(documentTypeId: null, deleted: true));
        Grant(
            VaultExtractPermissions.Documents.Default,
            VaultExtractPermissions.Documents.Restore,
            VaultExtractPermissions.Documents.ReadAll);

        var page = await AsPrincipalAsync(
            () => _appService.GetListAsync(new GetDocumentListInput { IsDeleted = true }));

        page.TotalCount.ShouldBe(2);
    }

    // ===================== List / export read scope =====================

    [Fact]
    public async Task GetListAsync_returns_only_the_granted_types_rows_and_a_matching_total_count()
    {
        StubQueryable(
            NewDocument(_typeA.Id), NewDocument(_typeA.Id), NewDocument(_typeB.Id), NewDocument(documentTypeId: null));

        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var page = await AsPrincipalAsync(() => _appService.GetListAsync(new GetDocumentListInput()));

        // The count is asserted alongside the rows on purpose: a filter applied only after CountAsync would give a
        // correct-looking page with a total that leaks how many documents the caller may not see.
        page.TotalCount.ShouldBe(2);
        page.Items.Count.ShouldBe(2);
        page.Items.ShouldAllBe(i => i.DocumentTypeCode == _typeA.TypeCode);
    }

    [Fact]
    public async Task GetListAsync_returns_nothing_for_a_caller_with_no_grant_at_all()
    {
        // An empty readable set is a real answer, not an absent filter — the distinction the query chain has an
        // explicit branch for.
        StubQueryable(NewDocument(_typeA.Id), NewDocument(_typeB.Id), NewDocument(documentTypeId: null));
        GrantEntryOnly();

        var page = await AsPrincipalAsync(() => _appService.GetListAsync(new GetDocumentListInput()));

        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetListAsync_is_unchanged_for_a_ReadAll_holder_including_untyped_rows()
    {
        StubQueryable(NewDocument(_typeA.Id), NewDocument(_typeB.Id), NewDocument(documentTypeId: null));
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        var page = await AsPrincipalAsync(() => _appService.GetListAsync(new GetDocumentListInput()));

        page.TotalCount.ShouldBe(3);
        page.Items.Count.ShouldBe(3);
    }

    /// <summary>
    /// #635: the export's rows are narrowed by the Read scope, the same predicate the operator list runs, so a
    /// caller holding a Read grant on A only gets A's rows in the file and none of B's — <b>as rows</b>, not as a
    /// refusal. #632's second, per-type gate ("throw unless you hold Read on this type") is gone: it could not
    /// survive ownership, because an uploader legitimately exports their own documents of a type they hold no
    /// grant on, so the gate would have had to admit every caller carrying an owner arm, which is every real user.
    /// </summary>
    [Fact]
    public async Task ExportAsync_narrows_its_rows_by_the_read_scope_rather_than_refusing_an_ungranted_type()
    {
        StubQueryable(NewDocument(_typeA.Id), NewDocument(_typeB.Id));
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Export);
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        var granted = await AsPrincipalAsync(() => _exportAppService.ExportAsync(new ExportDocumentsInput
        {
            DocumentTypeCode = _typeA.TypeCode,
            Format = ExportFormat.Csv
        }));
        RowCount(granted).ShouldBe(1);

        var ungranted = await AsPrincipalAsync(() => _exportAppService.ExportAsync(new ExportDocumentsInput
        {
            DocumentTypeCode = _typeB.TypeCode,
            Format = ExportFormat.Csv
        }));
        RowCount(ungranted).ShouldBe(0);
    }

    /// <summary>
    /// The escape #635 closes on this service: it carried a class-level <c>[Authorize(Documents.Export)]</c> and
    /// narrowed by a read scope that deliberately did not assert entry, so <c>Documents.Export</c> plus a Read
    /// grant bulk-downloaded out of an area the caller could not open.
    /// </summary>
    [Fact]
    public async Task ExportAsync_needs_entry_even_with_the_module_wide_Export_permission()
    {
        StubQueryable(NewDocument(_typeA.Id));
        Grant(VaultExtractPermissions.Documents.Export);
        GrantResource(VaultExtractResourcePermissions.Read, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _exportAppService.ExportAsync(new ExportDocumentsInput
            {
                DocumentTypeCode = _typeA.TypeCode,
                Format = ExportFormat.Csv
            })));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Export);

        var file = await AsPrincipalAsync(() => _exportAppService.ExportAsync(new ExportDocumentsInput
        {
            DocumentTypeCode = _typeA.TypeCode,
            Format = ExportFormat.Csv
        }));
        RowCount(file).ShouldBe(1);
    }

    /// <summary>Data rows in a CSV export, excluding the header line.</summary>
    private static int RowCount(IRemoteStreamContent file)
    {
        using var reader = new StreamReader(file.GetStream());
        var text = reader.ReadToEnd();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
    }

    // ===================== Entry (Documents.Default) is a precondition of every rule =====================

    /// <summary>
    /// The combination ordinary administration makes reachable and nothing asserted before: handing out a resource
    /// grant is gated by <c>DocumentTypes.ManagePermissions</c> alone, which is unrelated to <c>Documents.*</c>, so
    /// an admin can grant "Delete on Invoices" to a principal holding no <c>Documents</c> permission at all. Before
    /// #632's entry assertion that produced a caller who could soft-delete, restore and rewrite the Markdown of a
    /// document it could not read — every mutating family had swapped its <c>[Authorize]</c> for a bare per-type
    /// check and asserted entry nowhere.
    /// <para>
    /// Each fact grants entry at the end and shows the same call then succeeds. Without that half it would pass for
    /// any reason the call happened to fail, rather than for the missing entry permission.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_edit_needs_entry_even_with_an_Edit_grant_on_the_documents_own_type()
    {
        var document = StubDocument(_typeA.Id);
        GrantNothing();
        GrantResource(VaultExtractResourcePermissions.Edit, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" })));

        GrantEntryOnly();

        var dto = await AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" }));
        dto.Id.ShouldBe(document.Id);
    }

    [Fact]
    public async Task DeleteAsync_needs_entry_even_with_a_Delete_grant_on_the_documents_own_type()
    {
        var document = StubDocument(_typeA.Id);
        GrantNothing();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.DeleteAsync(document.Id)));
        await _documentRepository.DidNotReceive().DeleteAsync(
            document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        GrantEntryOnly();

        await AsPrincipalAsync(() => _appService.DeleteAsync(document.Id));
        await _documentRepository.Received(1).DeleteAsync(
            document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreAsync_needs_entry_even_with_a_Delete_grant_on_the_documents_own_type()
    {
        var document = StubDocument(_typeA.Id, deleted: true);
        GrantNothing();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.RestoreAsync(document.Id)));
        document.IsDeleted.ShouldBeTrue();

        GrantEntryOnly();

        await AsPrincipalAsync(() => _appService.RestoreAsync(document.Id));
        document.IsDeleted.ShouldBeFalse();
    }

    /// <summary>
    /// Entry gates the MODULE-WIDE half too, not only the per-type half. Such a principal can only be assembled
    /// programmatically — ABP's dialog grants the parent with the child, and <c>PermissionChecker</c> never
    /// consults <c>Parent</c> at check time — but #632 decision 1 says a caller who may not enter the documents
    /// area may not mutate documents in it either. Read already behaved this way (<c>GetAsync</c> asserts entry
    /// before the Read rule, whose module-wide half is <c>ReadAll</c>); this is the mutate families catching up.
    /// </summary>
    [Fact]
    public async Task The_module_wide_half_needs_entry_too()
    {
        var document = StubDocument(_typeA.Id);
        Grant(VaultExtractPermissions.Documents.Delete);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.DeleteAsync(document.Id)));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Delete);

        await AsPrincipalAsync(() => _appService.DeleteAsync(document.Id));
        await _documentRepository.Received(1).DeleteAsync(
            document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The recycle bin refuses a caller with no entry permission, and admits the same caller once entry is added.
    /// <para>
    /// #635 makes this a guard on the checker rather than only a statement about the endpoint: the recycle bin has
    /// no gate of its own any more, so the refusal comes from <c>ResolveScopeAsync</c>'s entry assertion and
    /// nothing else. Under #632 this fact stayed green with that assertion removed, because <c>GetListAsync</c>
    /// carried its own <c>CheckPolicyAsync(Documents.Default)</c> in front of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_recycle_bin_refuses_a_caller_with_no_entry_permission()
    {
        StubQueryable(NewDocument(_typeA.Id, deleted: true));
        GrantNothing();
        GrantResource(VaultExtractResourcePermissions.Delete, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(
            () => _appService.GetListAsync(new GetDocumentListInput { IsDeleted = true })));

        GrantEntryOnly();

        var page = await AsPrincipalAsync(
            () => _appService.GetListAsync(new GetDocumentListInput { IsDeleted = true }));
        page.TotalCount.ShouldBe(0);
    }

    // The declare-a-type family's entry fact lives in DocumentAppService_UploadDeclaredType_Tests, on
    // UploadAsync's declared-type branch — the rule's original call site, and the one with a working positive
    // half to contrast the refusal against.

    // ===================== Authorization outranks the business guards =====================

    /// <summary>
    /// #632: both authorization halves of Confirm / Reclassify now run before the <c>NotTextExtracted</c> business
    /// guard. This caller may edit type A but holds nothing on target type B, and the document has no Markdown — so
    /// while the guard sat between the two checks it answered <c>NotTextExtracted</c> here, reporting this
    /// document's processing state to a caller with no right on the type it is being assigned to. The same
    /// reordering <c>RestoreAsync</c> and <c>ResolveFieldValidationWarningsAsync</c> already carry.
    /// </summary>
    [Fact]
    public async Task Reclassify_denies_on_the_target_type_before_the_NotTextExtracted_guard_can_answer()
    {
        var document = StubDocument(_typeA.Id);
        document.Markdown.ShouldBeNullOrEmpty();
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Edit, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _appService.ReclassifyAsync(document.Id, new ReclassifyDocumentInput { DocumentTypeId = _typeB.Id })));
    }

    // ===================== helpers =====================

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    /// <summary>Entry only: the post-#632 shape of a caller narrowed to its per-type grants.</summary>
    private void GrantEntryOnly() => Grant(VaultExtractPermissions.Documents.Default);

    /// <summary>No standard permission at all — only whatever resource grants the fact adds on top.</summary>
    private void GrantNothing() => Grant();

    private void GrantResource(string permissionName, Guid documentTypeId)
    {
        _resourcePermissionStore.Grant(
            permissionName,
            VaultExtractResourcePermissions.Name,
            documentTypeId.ToString(),
            UserResourcePermissionValueProvider.ProviderName,
            UserId.ToString());
    }

    private static Document NewDocument(Guid? documentTypeId, bool deleted = false)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "tester",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "access.pdf"));

        if (documentTypeId.HasValue)
        {
            // DocumentTypeId has a Domain-private setter; this assembly is not in the Domain's InternalsVisibleTo
            // list, so reflection stands in for the classification pipeline, as the EF / MCP tests already do.
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId.Value);
        }

        // ISoftDelete's setter is public (RestoreAsync itself clears it), so the recycle-bin state needs no
        // reflection.
        document.IsDeleted = deleted;

        return document;
    }

    /// <summary>Registers a document on both loaders the enforcement points use (lean and field-stage).</summary>
    private Document StubDocument(Guid? documentTypeId, bool deleted = false)
    {
        var document = NewDocument(documentTypeId, deleted);
        _documentRepository.GetAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(document);
        _documentRepository.FindWithFieldValuesAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);
        return document;
    }

    private void StubQueryable(params Document[] documents)
    {
        _documentRepository.GetQueryableAsync().Returns(documents.AsQueryable());
    }

    private async Task<T> AsPrincipalAsync<T>(Func<Task<T>> action)
    {
        using (_principalAccessor.Change(Principal()))
        {
            return await action();
        }
    }

    private async Task AsPrincipalAsync(Func<Task> action)
    {
        using (_principalAccessor.Change(Principal()))
        {
            await action();
        }
    }

    // The only claim ABP's user resource-permission value provider reads.
    private static ClaimsPrincipal Principal()
        => new(new ClaimsIdentity(
            [new Claim(AbpClaimTypes.UserId, UserId.ToString())],
            authenticationType: "ApplicationTest"));
}
