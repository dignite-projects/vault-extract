using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #635 decision 8, through the real permission chain: <b>a machine identity has no owner.</b> A
/// client-credentials token carries no <c>sub</c>, so <c>ICurrentUser.Id</c> is null and the ownership arm can
/// never match, whatever <c>CreatorId</c> a document happens to carry. Such a principal reaches documents only
/// through a module-wide permission or a per-type grant.
/// <para>
/// This has to be a real-chain fact rather than an Application-layer one: the claim shape is the whole point, and
/// every suite that fakes the authorization service decides the answer before ABP's claim-reading value providers
/// see the principal. <see cref="McpPermissionPipelineTestModule"/> is the only host in the repo that runs them —
/// no <c>AddAlwaysAllowAuthorization</c>, real <c>PermissionManagement</c>, real grant rows in SQLite.
/// </para>
/// <para>
/// It also settles the #563 question the Issue points at: if MCP ingest ever uses machine tokens, the documents
/// it creates have no owner. Today's MCP client (<c>VaultExtract_Mcp</c>, #281) is Authorization Code + PKCE, so
/// real MCP callers are users and do carry ownership.
/// </para>
/// </summary>
public class MachineIdentityOwnership_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    private const string ClientId = "machine-identity-635";
    private const string UserProviderName = "U";
    private const string ClientProviderName = "C";

    private static readonly Guid HumanOwnerId = Guid.Parse("11111111-0000-0000-0000-000000000635");

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public MachineIdentityOwnership_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    /// <summary>
    /// The machine principal holds entry and nothing else. The document carries an explicit <c>CreatorId</c>, so
    /// the ownership arm has something to compare against — and still does not match, because the caller has no
    /// user id at all. The human whose id that is reads the same document with the same grants, which is what
    /// makes this a fact about the missing <c>sub</c> rather than about the missing permission.
    /// <para>
    /// <b>The human goes first</b>, and holds a real per-type <c>Read</c> grant of their own. That ordering is
    /// what gives this fact teeth: the grant memo is scoped, both principals run inside the same scope, and if it
    /// did not reset on an identity change the machine would inherit the human's answers and read the document.
    /// The original version of this test ran the machine first and could not have detected that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_principal_with_no_user_id_never_matches_the_ownership_arm_or_inherits_the_previous_one()
    {
        Guid documentId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeId = await SeedTypeAsync("machine.owner");
            documentId = await SeedDocumentAsync(typeId, creatorId: HumanOwnerId);

            await GrantToClientAsync(VaultExtractPermissions.Documents.Default);
            await GrantToUserAsync(HumanOwnerId, VaultExtractPermissions.Documents.Default);
            // A real per-type grant for the human only. If the memo leaked across the principal switch below, the
            // machine would read this document through THIS row.
            await GrantResourceToUserAsync(HumanOwnerId, VaultExtractResourcePermissions.Read, typeId);
        });

        // The human first, so the memo is populated with a principal that CAN read before the machine asks.
        using (_principalAccessor.Change(UserPrincipal(HumanOwnerId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var dto = await _documentAppService.GetAsync(documentId);
                dto.Id.ShouldBe(documentId);
                dto.Rights.CanRead.ShouldBeTrue();

                var page = await _documentAppService.GetListAsync(new GetDocumentListInput());
                page.TotalCount.ShouldBe(1);
            });
        }

        using (_principalAccessor.Change(MachinePrincipal()))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.GetAsync(documentId)));

            // And the list it is admitted to is empty, for the same reason: the scope has no owner arm to build
            // from, and no grant of its own to fall back on.
            await WithUnitOfWorkAsync(async () =>
            {
                var page = await _documentAppService.GetListAsync(new GetDocumentListInput());
                page.TotalCount.ShouldBe(0);
            });
        }
    }

    // ---- seeding / grant helpers (same shape as McpPerTypeGrantPipeline_Tests) ----

    private async Task<Guid> SeedTypeAsync(string typeCode)
    {
        var id = Guid.NewGuid();
        await _documentTypeRepository.InsertAsync(
            new DocumentType(id, tenantId: null, typeCode, typeCode), autoSave: true);
        return id;
    }

    private async Task<Guid> SeedDocumentAsync(Guid? documentTypeId, Guid? creatorId)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "svc",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "machine.pdf"));

        if (documentTypeId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId.Value);
        }

        if (creatorId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
        }

        await _documentRepository.InsertAsync(document, autoSave: true);
        return document.Id;
    }

    private Task GrantToUserAsync(Guid userId, string permissionName)
        => GrantAsync(permissionName, UserProviderName, userId.ToString());

    /// <summary>A real row in <c>AbpResourcePermissionGrants</c>, resolved by ABP's own user value provider.</summary>
    private async Task GrantResourceToUserAsync(Guid userId, string permissionName, Guid documentTypeId)
    {
        await GetRequiredService<IResourcePermissionGrantRepository>().InsertAsync(
            new ResourcePermissionGrant(
                _guidGenerator.Create(),
                permissionName,
                VaultExtractResourcePermissions.Name,
                documentTypeId.ToString(),
                UserProviderName,
                userId.ToString(),
                tenantId: null),
            autoSave: true);
    }

    private Task GrantToClientAsync(string permissionName)
        => GrantAsync(permissionName, ClientProviderName, ClientId);

    private async Task GrantAsync(string permissionName, string providerName, string providerKey)
    {
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(_guidGenerator.Create(), permissionName, providerName, providerKey, tenantId: null),
            autoSave: true);
    }

    /// <summary>A client-credentials principal: a client id, and deliberately no <c>AbpClaimTypes.UserId</c>.</summary>
    private static ClaimsPrincipal MachinePrincipal()
        => new(new ClaimsIdentity(
            [new Claim(AbpClaimTypes.ClientId, ClientId)],
            authenticationType: "IntegrationTest"));

    private static ClaimsPrincipal UserPrincipal(Guid userId)
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, userId.ToString()),
                new Claim(AbpClaimTypes.UserName, "machine-identity-test")
            ],
            authenticationType: "IntegrationTest"));
}
