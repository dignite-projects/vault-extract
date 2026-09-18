using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #645: a rule's role-level arm is a <b>set</b> of module-wide permissions, and the checker admits when
/// <b>any</b> member is granted. Stated against a rule built for the purpose rather than a production row, so the
/// facts are about the checker's evaluation and cannot pass because one particular row happens to be shaped a
/// particular way. The production rows are pinned by <see cref="DocumentAccessRuleTable_Tests"/>.
/// </summary>
public class DocumentAccessRoleLevelSet_Tests : DocumentAccessTestBase
{
    private static readonly DocumentAccessRule EitherOfTwo = new(
        [VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractPermissions.Documents.Upload],
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);

    private readonly DocumentAccessChecker _checker;

    public DocumentAccessRoleLevelSet_Tests()
    {
        _checker = GetRequiredService<DocumentAccessChecker>();
    }

    [Fact]
    public async Task Either_member_of_the_set_admits_on_its_own()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ConfirmClassification);
        (await IsGrantedAsync(EitherOfTwo)).ShouldBeTrue();

        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);
        (await IsGrantedAsync(EitherOfTwo)).ShouldBeTrue();
    }

    [Fact]
    public async Task No_member_of_the_set_refuses()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        (await IsGrantedAsync(EitherOfTwo)).ShouldBeFalse();
    }

    /// <summary>Entry still gates the whole rule: holding every member of the set is not enough without it.</summary>
    [Fact]
    public async Task Every_member_of_the_set_without_entry_refuses()
    {
        Grant(VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractPermissions.Documents.Upload);

        (await IsGrantedAsync(EitherOfTwo)).ShouldBeFalse();
    }

    /// <summary>
    /// The scope shape reads the same set: a member admits the whole layer.
    /// </summary>
    [Fact]
    public async Task A_member_of_the_set_resolves_an_unrestricted_scope()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        var scope = await AsStrangerAsync(() => _checker.ResolveScopeAsync(EitherOfTwo));

        scope.ShouldBeSameAs(DocumentAccessScope.Unrestricted);
    }

    /// <summary>
    /// The memo still asks each standard permission name at most once per request, set or no set: asking the
    /// same two-member rule three times, for a caller holding neither member, costs entry plus the two members —
    /// three checks, not seven.
    /// </summary>
    [Fact]
    public async Task Each_member_of_the_set_is_asked_at_most_once_per_request()
    {
        GrantEntryOnly();
        Authorization.ResetPolicyChecks();

        await AsStrangerAsync(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                (await _checker.IsGrantedAsync(EitherOfTwo, DocumentAccessSubject.None)).ShouldBeFalse();
            }
        });

        Authorization.PolicyChecks.ShouldBe(3);
    }

    private Task<bool> IsGrantedAsync(DocumentAccessRule rule)
        => AsStrangerAsync(() => _checker.IsGrantedAsync(rule, DocumentAccessSubject.None));
}
