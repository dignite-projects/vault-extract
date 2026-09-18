using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// #635 decision 8: <b>derived sub-documents inherit the origin's owner</b>, and the explicitly assigned
/// <c>CreatorId</c> survives <c>SaveChanges</c>.
/// <para>
/// The claim this pins is about ABP, not about this repository: <c>AuditPropertySetter.SetCreatorId</c> returns
/// early when <c>CreatorId</c> already has a value, so a value <c>Document.CreateDerived</c> assigned is not
/// overwritten by the ambient principal on insert. Nothing in the application layer can assert that — the setter
/// runs inside <c>AbpDbContext.SaveChangesAsync</c> — so it takes a real provider and a real save.
/// </para>
/// <para>
/// The adversarial half is the second fact: a <b>different</b> principal is ambient while the derived document is
/// inserted, which is exactly the shape that would silently reassign ownership if the early return ever stopped
/// happening. In production the principal is simply absent (segmentation runs in a background job), so a test
/// that saved with no principal at all would pass whether or not the guarantee held.
/// </para>
/// </summary>
public class DerivedDocumentOwnership_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid OriginOwnerId = Guid.Parse("11111111-1111-1111-1111-000000000635");
    private static readonly Guid SomebodyElseId = Guid.Parse("22222222-2222-2222-2222-000000000635");

    private readonly IDocumentRepository _documentRepository;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public DerivedDocumentOwnership_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    [Fact]
    public async Task An_explicitly_assigned_CreatorId_survives_SaveChanges_under_another_principal()
    {
        var derivedId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var derived = Document.CreateDerived(
                derivedId,
                tenantId: null,
                fileOrigin: null,
                originDocumentId: Guid.NewGuid(),
                originConstituentKey: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                creatorId: OriginOwnerId);

            using (_principalAccessor.Change(Principal(SomebodyElseId)))
            {
                await _documentRepository.InsertAsync(derived, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(derivedId, includeDetails: false);
            reloaded.CreatorId.ShouldBe(OriginOwnerId);
        });
    }

    /// <summary>
    /// The counter-case, so the fact above cannot pass because the audit setter simply never runs in this test
    /// host: with <c>CreatorId</c> left unassigned, the ambient principal <b>is</b> written. That is the behaviour
    /// the explicit assignment is relied upon to override.
    /// </summary>
    [Fact]
    public async Task An_unassigned_CreatorId_is_still_filled_from_the_ambient_principal()
    {
        var documentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(
                documentId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"{Guid.NewGuid():N}.pdf",
                    uploadedByUserName: "audit",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    fileSize: 1024,
                    originalFileName: "audit.pdf"));

            using (_principalAccessor.Change(Principal(SomebodyElseId)))
            {
                await _documentRepository.InsertAsync(document, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(documentId, includeDetails: false);
            reloaded.CreatorId.ShouldBe(SomebodyElseId);
        });
    }

    /// <summary>
    /// A source that nobody owns spawns sub-documents that nobody owns; nothing invents an owner. Saved under a
    /// principal with no user-id claim, which is the shape a background job actually has — the test host carries
    /// its own ambient principal, so "pass no principal" would not have produced this state.
    /// </summary>
    [Fact]
    public async Task A_null_origin_owner_stays_null_under_a_principal_with_no_user_id()
    {
        var derivedId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var derived = Document.CreateDerived(
                derivedId,
                tenantId: null,
                fileOrigin: null,
                originDocumentId: Guid.NewGuid(),
                originConstituentKey: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                creatorId: null);

            using (_principalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity())))
            {
                await _documentRepository.InsertAsync(derived, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(derivedId, includeDetails: false);
            reloaded.CreatorId.ShouldBeNull();
        });
    }

    private static ClaimsPrincipal Principal(Guid userId)
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, userId.ToString()),
                new Claim(AbpClaimTypes.UserName, "ownership-test")
            ],
            authenticationType: "EfCoreTest"));
}
