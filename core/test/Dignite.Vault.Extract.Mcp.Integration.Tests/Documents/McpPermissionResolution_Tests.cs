using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Volo.Abp.Users;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #516: the MCP egress's fail-closed permission gate, verified end-to-end against a REAL ABP permission pipeline
/// (no <c>AddAlwaysAllowAuthorization</c>; real <c>PermissionChecker</c> + EF <c>PermissionStore</c>). This is the
/// coverage the deleted #432 project held, which #514 removed together with the X-Api-Key channel it was built
/// around — re-established here without that channel: the principal is a <b>bare</b> <see cref="ClaimsPrincipal"/>
/// carrying only <c>AbpClaimTypes.UserId</c> (all ABP permission resolution reads) plus a non-empty authentication
/// type, so the test is independent of how an authenticated caller was produced. These tests drive the real
/// <c>search_documents</c> / <c>list_cabinets</c> tools as that principal and assert:
///   (a) granted the minimal <c>Documents.Default</c> -> <c>CheckPolicyAsync</c> passes and a search returns rows;
///   (b) NOT granted -> fail-closed <see cref="AbpAuthorizationException"/> (the LLM tool-dispatch path is not a privilege-escalation channel);
///   (c) <c>CurrentUser.Id</c> == the service account.
/// </summary>
public class McpPermissionResolution_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    private const string TypeCode = "contract.integration";

    // The service account that is granted the permission, and a second, ungranted account for the fail-closed case.
    private static readonly Guid GrantedServiceAccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UngrantedServiceAccountId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IDocumentAppService _documentAppService;
    private readonly ICabinetReadAppService _cabinetReadAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly ICurrentUser _currentUser;

    public McpPermissionResolution_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _cabinetReadAppService = GetRequiredService<ICabinetReadAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _currentUser = GetRequiredService<ICurrentUser>();
    }

    [Fact]
    public async Task Granted_principal_resolves_permissions_and_search_returns_rows()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAndDocumentAsync();
            await GrantDocumentsDefaultAsync(GrantedServiceAccountId);
        });

        using (_principalAccessor.Change(ServiceAccountPrincipal(GrantedServiceAccountId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                // (c) the ambient principal really is the mapped service account.
                _currentUser.Id.ShouldBe(GrantedServiceAccountId);

                // (a) drive the real MCP tool: CheckPolicyAsync(Documents.Default) inside GetListAsync passes and
                // the search returns the seeded document — proving the store grant resolves from the user-id claim.
                var result = await DocumentSearchTool.SearchAsync(_documentAppService, documentTypeCode: TypeCode);

                result.Items.ShouldNotBeEmpty();
                result.Items.ShouldContain(i => i.DocumentTypeCode == TypeCode);
            });
        }
    }

    [Fact]
    public async Task Ungranted_principal_is_fail_closed_denied()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAndDocumentAsync();
            // Grant the OTHER account only, so the store has grants but not for this principal — proving the check
            // is keyed on the principal's user id, not merely "some grant exists".
            await GrantDocumentsDefaultAsync(GrantedServiceAccountId);
        });

        using (_principalAccessor.Change(ServiceAccountPrincipal(UngrantedServiceAccountId)))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => DocumentSearchTool.SearchAsync(_documentAppService, documentTypeCode: TypeCode)));
        }
    }

    [Fact]
    public async Task Documents_only_principal_can_discover_cabinets_without_cabinet_admin_permission()
    {
        var cabinetId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _cabinetRepository.InsertAsync(
                new Cabinet(cabinetId, tenantId: null, "Legal", "Contracts"),
                autoSave: true);
            await GrantDocumentsDefaultAsync(GrantedServiceAccountId);
        });

        using (_principalAccessor.Change(ServiceAccountPrincipal(GrantedServiceAccountId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                // #473 permission decision: cabinet discovery is document-read metadata. A service account with
                // exactly Documents.Default and no Cabinets.* grant can still discover cabinets.
                var result = await CabinetTools.ListAsync(_cabinetReadAppService);

                result.Items.ShouldContain(c => c.Id == cabinetId);
            });
        }
    }

    // Seeds the searched document type plus one document classified to it (DocumentTypeId has a Domain private
    // setter; mirror the EF search tests and set it via reflection to simulate the classified state).
    private async Task SeedTypeAndDocumentAsync()
    {
        var typeId = Guid.NewGuid();
        await _documentTypeRepository.InsertAsync(
            new DocumentType(typeId, tenantId: null, TypeCode, "Integration Contract"), autoSave: true);

        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "svc",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "integration.pdf"));

        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, typeId);

        await _documentRepository.InsertAsync(document, autoSave: true);
    }

    // ABP's user-scoped permission value provider name (UserPermissionValueProvider.ProviderName == "U"). We
    // write the grant row straight to the store rather than through IPermissionManager.SetAsync, because the "U"
    // *management* provider (the SetAsync dispatch target) lives in the Identity-integration package; the *check*
    // still runs through the real UserPermissionValueProvider + PermissionStore, which is the seam under test.
    private const string UserProviderName = "U";

    private async Task GrantDocumentsDefaultAsync(Guid userId)
    {
        // Direct user-level grant (provider "U", no roles) — exactly the least-privilege model the egress requires.
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(
                _guidGenerator.Create(),
                VaultExtractPermissions.Documents.Default,
                UserProviderName,
                userId.ToString(),
                tenantId: null),
            autoSave: true);
    }

    // The claim shape ABP's permission pipeline actually reads: only AbpClaimTypes.UserId, plus a non-empty
    // authentication type so the identity is IsAuthenticated == true. Permissions are NOT stamped as claims — the
    // real PermissionChecker resolves them from the store by user id at CheckPolicyAsync time. This is the shape
    // any authenticated MCP caller carries; the assertion under test is independent of how it was obtained.
    private static ClaimsPrincipal ServiceAccountPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(AbpClaimTypes.UserId, userId.ToString()) },
            authenticationType: "IntegrationTest");
        return new ClaimsPrincipal(identity);
    }
}
