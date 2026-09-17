using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using ModelContextProtocol;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #636 acceptance, the real-chain half of <see cref="IDocumentAppService.FindForCallerAsync"/>'s fold, exercised
/// through the actual MCP adapter (<see cref="DocumentTools.GetAsync"/>) rather than a mocked AppService — the
/// same real permission pipeline <see cref="McpPerTypeGrantPipeline_Tests"/> uses (no
/// <c>AddAlwaysAllowAuthorization</c>, a real <c>IResourcePermissionChecker</c> reading a real
/// <c>AbpResourcePermissionGrants</c> row).
/// <para>
/// Two facts, matching the two halves of the fold: a caller with no entry (<c>Documents.Default</c>) at all must
/// NOT read like "not found" — that would make an unprivileged client indistinguishable from a working one that
/// got unlucky with an id — while a caller who has entry but no per-type <c>Read</c> grant on the document's type
/// must answer exactly like a nonexistent id, closing the existence-disclosure #632 already closed for
/// <c>GetAsync</c>'s own callers.
/// </para>
/// </summary>
public class McpDocumentAccessFolding_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    private static readonly Guid NoEntryCallerId = Guid.Parse("66666666-0000-0000-0000-000000000636");
    private static readonly Guid NoTypeGrantCallerId = Guid.Parse("77777777-0000-0000-0000-000000000636");

    private const string UserProviderName = "U";

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public McpDocumentAccessFolding_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    /// <summary>
    /// A caller with no <c>Documents.Default</c> grant at all gets an authorization-class failure, not the
    /// "Document not found" every other adapter refusal answers with. <c>FindForCallerAsync</c> asserts entry
    /// before it ever looks at the id, so this must be true regardless of whether the id names a real document.
    /// </summary>
    [Fact]
    public async Task Caller_with_no_entry_gets_an_authorization_error_not_a_not_found()
    {
        Guid documentId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeId = await SeedTypeAsync("mcp.fold.no-entry");
            documentId = await SeedDocumentAsync(typeId);
            // Deliberately grants nothing to NoEntryCallerId.
        });

        using (_principalAccessor.Change(Principal(NoEntryCallerId)))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => DocumentTools.GetAsync(documentId.ToString(), _documentAppService)));
        }
    }

    /// <summary>
    /// A caller with entry but no per-type <c>Read</c> grant on the document's type (and no module-wide
    /// <c>Documents.ReadAll</c>) still gets "Document not found" — the same answer a nonexistent id gets. This is
    /// the #632 disclosure guard, now verified through <c>FindForCallerAsync</c> and the real adapter rather than
    /// a mocked <c>GetAsync</c> throw.
    /// </summary>
    [Fact]
    public async Task Caller_with_entry_but_no_type_grant_gets_document_not_found()
    {
        Guid documentId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeId = await SeedTypeAsync("mcp.fold.no-type-grant");
            documentId = await SeedDocumentAsync(typeId);

            await GrantAsync(NoTypeGrantCallerId, VaultExtractPermissions.Documents.Default);
            // Deliberately grants no Read resource permission on typeId and no Documents.ReadAll.
        });

        using (_principalAccessor.Change(Principal(NoTypeGrantCallerId)))
        {
            var ex = await Should.ThrowAsync<McpException>(() =>
                WithUnitOfWorkAsync(() => DocumentTools.GetAsync(documentId.ToString(), _documentAppService)));

            ex.Message.ShouldBe($"Document not found: {documentId}");
        }
    }

    // ---- seeding / grant helpers (mirrors McpPerTypeGrantPipeline_Tests) ----

    private async Task<Guid> SeedTypeAsync(string typeCode)
    {
        var id = Guid.NewGuid();
        await _documentTypeRepository.InsertAsync(
            new DocumentType(id, tenantId: null, typeCode, typeCode), autoSave: true);
        return id;
    }

    private async Task<Guid> SeedDocumentAsync(Guid documentTypeId)
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
                originalFileName: "mcp-fold.pdf"));

        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId);

        await _documentRepository.InsertAsync(document, autoSave: true);
        return document.Id;
    }

    private async Task GrantAsync(Guid userId, string permissionName)
    {
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(
                _guidGenerator.Create(), permissionName, UserProviderName, userId.ToString(), tenantId: null),
            autoSave: true);
    }

    private static ClaimsPrincipal Principal(Guid userId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(AbpClaimTypes.UserId, userId.ToString()),
                new Claim(AbpClaimTypes.UserName, "mcp-fold-test")
            },
            authenticationType: "IntegrationTest"));
    }
}
