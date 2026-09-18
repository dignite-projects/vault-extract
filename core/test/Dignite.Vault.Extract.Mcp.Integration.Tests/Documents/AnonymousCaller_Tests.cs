using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #635: the three documents-domain app services carry no <c>[Authorize]</c> attribute at all any more, not even
/// a class-level one, and <c>VaultExtractAppService</c> declares none either — so "must be authenticated" is
/// asserted nowhere by attribute.
/// <para>
/// It does not need to be. ABP's permission value providers key on the principal's claims —
/// <c>UserPermissionValueProvider</c> on the user id, <c>RolePermissionValueProvider</c> on the roles,
/// <c>ClientPermissionValueProvider</c> on the client id — and a principal with no identity carries none, so
/// entry is simply not granted and the first checker call refuses. The reasoning is invisible at every call site,
/// which is why it has a fact of its own.
/// </para>
/// <para>
/// <b>It has to be here, not in Application.Tests.</b> That suite replaces the authorization service with
/// <c>GrantSetAuthorizationService</c>, which answers from a grant set and never looks at the principal — so an
/// anonymous caller and an authenticated one with an empty grant set are indistinguishable to it, and a fact
/// written there would pass whatever ABP did with the claims. <see cref="McpPermissionPipelineTestModule"/> is
/// the only host in the repo that runs the real pipeline.
/// </para>
/// </summary>
public class AnonymousCaller_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public AnonymousCaller_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    [Fact]
    public async Task A_principal_with_no_identity_is_refused_by_every_documents_entry_point()
    {
        Guid documentId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeId = Guid.NewGuid();
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, "anon.type", "Anon"), autoSave: true);
            documentId = await SeedDocumentAsync(typeId);
        });

        using (_principalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity())))
        {
            // A read, a list and a mutating method, so the refusal does not depend on which shape the call takes.
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.GetAsync(documentId)));
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.GetListAsync(new GetDocumentListInput())));
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.DeleteAsync(documentId)));
        }

        // And the document is still there, so the refusals were refusals rather than silent no-ops.
        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(documentId)).ShouldNotBeNull());
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
                originalFileName: "anon.pdf"));

        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId);

        await _documentRepository.InsertAsync(document, autoSave: true);
        return document.Id;
    }
}
