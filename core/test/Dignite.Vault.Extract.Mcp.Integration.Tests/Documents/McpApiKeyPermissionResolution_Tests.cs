using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Mcp.Authentication;
using Dignite.Vault.Extract.Mcp.Documents;
using Dignite.Vault.Extract.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Volo.Abp.Users;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #432: the single riskiest claim of the API-key channel, verified end-to-end against a REAL ABP permission
/// pipeline (no <c>AddAlwaysAllowAuthorization</c>; real <c>PermissionChecker</c> + EF <c>PermissionStore</c>).
///
/// The #430 TestServer tests proved the middleware builds the right principal and the HTTP fall-through shape, but
/// used a stub auth scheme — they could not prove that a hand-crafted service-account principal (carrying ONLY
/// <c>AbpClaimTypes.UserId</c> + the <c>McpApiKey</c> authentication type, and <b>no</b> permission claims) actually
/// resolves grants through ABP's permission checker. These tests drive the real <c>search_documents</c> tool as
/// that exact principal (built by the shared <see cref="McpApiKeyPrincipalFactory"/>, so the test cannot drift from
/// the runtime) and assert:
///   (a) granted the minimal <c>Documents.Default</c> -> <c>CheckPolicyAsync</c> passes and a search returns rows;
///   (b) NOT granted -> fail-closed <see cref="AbpAuthorizationException"/> (the LLM path is not a privilege-escalation channel);
///   (c) <c>CurrentUser.Id</c> == the mapped service account.
/// </summary>
public class McpApiKeyPermissionResolution_Tests : McpApiKeyIntegrationTestBase<McpApiKeyIntegrationTestModule>
{
    private const string TypeCode = "contract.integration";

    // The service account the key maps to (granted) and a second, ungranted account for the fail-closed case.
    private static readonly Guid GrantedServiceAccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UngrantedServiceAccountId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly ICurrentUser _currentUser;

    public McpApiKeyPermissionResolution_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _currentUser = GetRequiredService<ICurrentUser>();
    }

    [Fact]
    public async Task Granted_key_principal_resolves_permissions_and_search_returns_rows()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAndDocumentAsync();
            await GrantDocumentsDefaultAsync(GrantedServiceAccountId);
        });

        using (_principalAccessor.Change(ApiKeyPrincipal(GrantedServiceAccountId)))
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
    public async Task Ungranted_key_principal_is_fail_closed_denied()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAndDocumentAsync();
            // Grant the OTHER account only, so the store has grants but not for this principal — proving the check
            // is keyed on the principal's user id, not merely "some grant exists".
            await GrantDocumentsDefaultAsync(GrantedServiceAccountId);
        });

        using (_principalAccessor.Change(ApiKeyPrincipal(UngrantedServiceAccountId)))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => DocumentSearchTool.SearchAsync(_documentAppService, documentTypeCode: TypeCode)));
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
    // still runs through the real UserPermissionValueProvider + PermissionStore, which is the seam #432 verifies.
    private const string UserProviderName = "U";

    private async Task GrantDocumentsDefaultAsync(Guid userId)
    {
        // Direct user-level grant (provider "U", no roles) — exactly the least-privilege model the channel requires.
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(
                _guidGenerator.Create(),
                VaultExtractPermissions.Documents.Default,
                UserProviderName,
                userId.ToString(),
                tenantId: null),
            autoSave: true);
    }

    // The exact principal the request middleware builds for a matched key — via the shared factory so this test
    // exercises the real claim shape (UserId + McpApiKey auth type, no permission claims), not a copy.
    private static ClaimsPrincipal ApiKeyPrincipal(Guid serviceAccountUserId)
    {
        return McpApiKeyPrincipalFactory.Create(new McpApiKeyEntry
        {
            Key = "integration-test-key-not-used-for-matching-0000",
            ServiceAccountUserId = serviceAccountUserId.ToString()
        });
    }
}
