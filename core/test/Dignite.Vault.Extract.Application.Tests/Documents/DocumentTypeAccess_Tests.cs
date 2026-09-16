using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
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
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeA.Id);

        var dto = await AsPrincipalAsync(() => _appService.GetAsync(document.Id));

        dto.Id.ShouldBe(document.Id);
        dto.DocumentTypeCode.ShouldBe(_typeA.TypeCode);
    }

    [Fact]
    public async Task GetAsync_is_denied_for_a_document_of_another_type()
    {
        var document = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeA.Id);

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
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeA.Id);
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeB.Id);

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
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeA.Id);

        var stream = await AsPrincipalAsync(() => _appService.GetBlobAsync(granted.Id));
        stream.ShouldNotBeNull();

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsPrincipalAsync(() => _appService.GetBlobAsync(denied.Id)));
    }

    // ===================== Edit =====================

    [Fact]
    public async Task An_edit_is_admitted_by_an_Edit_grant_on_the_documents_own_type_and_denied_on_another()
    {
        var granted = StubDocument(_typeA.Id);
        var denied = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Edit, _typeA.Id);

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
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Edit, _typeA.Id);

        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" })));

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ConfirmClassification);

        var dto = await AsPrincipalAsync(() =>
            _appService.RejectReviewAsync(document.Id, new RejectReviewInput { Reason = "x" }));
        dto.Id.ShouldBe(document.Id);
    }

    /// <summary>
    /// The edit family had to LOSE its <c>[Authorize(ConfirmClassification)]</c> attributes, not merely gain a body
    /// check: the attribute fires before the body and would deny a per-type Edit holder outright, making the OR
    /// unreachable. Re-adding one would break exactly one grant path and nothing else, which is the kind of
    /// regression a behavioural test on one method does not catch.
    /// </summary>
    [Fact]
    public void Every_edit_family_method_lost_its_module_wide_Authorize_attribute()
    {
        string[] editFamily =
        [
            nameof(IDocumentAppService.ConfirmClassificationAsync),
            nameof(IDocumentAppService.ReclassifyAsync),
            nameof(IDocumentAppService.RerecognizeAsync),
            nameof(IDocumentAppService.ReextractFieldsAsync),
            nameof(IDocumentAppService.UpdateExtractedFieldsAsync),
            nameof(IDocumentAppService.UpdateMarkdownAsync),
            nameof(IDocumentAppService.RejectReviewAsync),
            nameof(IDocumentAppService.AllowDuplicateAsync),
            nameof(IDocumentAppService.ResolveFieldValidationWarningsAsync),
            nameof(IDocumentAppService.DeleteAsync)
        ];

        foreach (var name in editFamily)
        {
            var method = typeof(DocumentAppService).GetMethod(name).ShouldNotBeNull();
            method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .ShouldBeEmpty($"{name} must not carry [Authorize]: it would short-circuit the per-type OR.");
        }

        // The counter-case, so this test cannot pass by the attribute type simply never being found: the
        // module-wide-only operations still carry theirs.
        typeof(DocumentAppService).GetMethod(nameof(IDocumentAppService.RestoreAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).ShouldNotBeEmpty();
        typeof(DocumentAppService).GetMethod(nameof(IDocumentAppService.PermanentDeleteAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).ShouldNotBeEmpty();
    }

    // ===================== Delete =====================

    [Fact]
    public async Task DeleteAsync_is_admitted_by_a_Delete_grant_on_the_documents_own_type_and_denied_on_another()
    {
        var granted = StubDocument(_typeA.Id);
        var denied = StubDocument(_typeB.Id);
        GrantEntryOnly();
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Delete, _typeA.Id);

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

    // ===================== List / export read scope =====================

    [Fact]
    public async Task GetListAsync_returns_only_the_granted_types_rows_and_a_matching_total_count()
    {
        StubQueryable(
            NewDocument(_typeA.Id), NewDocument(_typeA.Id), NewDocument(_typeB.Id), NewDocument(documentTypeId: null));

        GrantEntryOnly();
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeA.Id);

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

    [Fact]
    public async Task ExportAsync_is_admitted_for_a_granted_type_and_refused_for_another()
    {
        StubQueryable(NewDocument(_typeA.Id), NewDocument(_typeB.Id));
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Export);
        GrantResource(VaultExtractPermissions.DocumentTypes.Resources.Read, _typeA.Id);

        var file = await AsPrincipalAsync(() => _exportAppService.ExportAsync(new ExportDocumentsInput
        {
            DocumentTypeCode = _typeA.TypeCode,
            Format = ExportFormat.Csv
        }));
        file.ShouldNotBeNull();

        // Refused loudly rather than handed back a header-only file: this service's own doctrine is that an empty
        // export "is a silent lie about what the layer contains", and that holds whether the rows are missing
        // because none exist or because the caller may not read them.
        await Should.ThrowAsync<AbpAuthorizationException>(() => AsPrincipalAsync(() =>
            _exportAppService.ExportAsync(new ExportDocumentsInput
            {
                DocumentTypeCode = _typeB.TypeCode,
                Format = ExportFormat.Csv
            })));
    }

    // ===================== helpers =====================

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    /// <summary>Entry only: the post-#632 shape of a caller narrowed to its per-type grants.</summary>
    private void GrantEntryOnly() => Grant(VaultExtractPermissions.Documents.Default);

    private void GrantResource(string permissionName, Guid documentTypeId)
    {
        _resourcePermissionStore.Grant(
            permissionName,
            VaultExtractPermissions.DocumentTypes.Resources.Name,
            documentTypeId.ToString(),
            UserResourcePermissionValueProvider.ProviderName,
            UserId.ToString());
    }

    private static Document NewDocument(Guid? documentTypeId)
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

        return document;
    }

    /// <summary>Registers a document on both loaders the enforcement points use (lean and field-stage).</summary>
    private Document StubDocument(Guid? documentTypeId)
    {
        var document = NewDocument(documentTypeId);
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
