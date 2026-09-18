using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Shouldly;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// #635 decision 3, against a <b>real</b> EF provider: <see cref="DocumentAccessScope.ToPredicate"/> has to be an
/// expression the relational provider can translate, for all three shapes.
/// <para>
/// The Domain-layer <c>DocumentReadScopeFilter_Tests</c> runs the same predicate over an in-memory
/// <c>AsQueryable()</c>, which happily evaluates expressions no database can — a set membership through an
/// interface the provider does not recognise, or a nullable comparison it folds differently — so it can only pin
/// the predicate's <i>semantics</i>. What is under test here is that the same semantics survive translation to
/// SQL, and in particular that the <b>untyped-but-owned</b> row (<c>DocumentTypeId IS NULL</c> AND
/// <c>CreatorId = @owner</c>) comes back: it is the row that a naive
/// <c>DocumentTypeId.HasValue &amp;&amp; …</c> predicate silently drops, and the one #635 exists to make
/// reachable.
/// </para>
/// </summary>
public class DocumentAccessScopePredicate_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-000000000635");
    private static readonly Guid Stranger = Guid.Parse("22222222-2222-2222-2222-000000000635");

    private readonly IDocumentRepository _documentRepository;

    private Guid _typeA;
    private Guid _typeB;
    private Guid _ownTypeA;
    private Guid _strangerTypeA;
    private Guid _ownTypeB;
    private Guid _ownUntyped;
    private Guid _strangerUntyped;

    public DocumentAccessScopePredicate_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task An_unrestricted_scope_translates_to_no_narrowing_at_all()
    {
        await SeedAsync();

        var ids = await QueryAsync(DocumentAccessScope.Unrestricted);

        ids.ShouldBe(
            [_ownTypeA, _strangerTypeA, _ownTypeB, _ownUntyped, _strangerUntyped],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_type_only_scope_translates_to_a_set_membership_over_the_type_column()
    {
        await SeedAsync();

        var ids = await QueryAsync(Scope(ownerId: null, _typeA));

        ids.ShouldBe([_ownTypeA, _strangerTypeA], ignoreOrder: true);
    }

    /// <summary>
    /// Types OR owner, including the untyped-but-owned row. A <c>CreatorId = @owner</c> comparison must not pull
    /// in the rows whose <c>CreatorId</c> is NULL either, which is what the stranger's untyped row checks.
    /// </summary>
    [Fact]
    public async Task A_types_plus_owner_scope_translates_to_the_union_including_the_untyped_owned_row()
    {
        await SeedAsync();

        var ids = await QueryAsync(Scope(Owner, _typeA));

        ids.ShouldBe([_ownTypeA, _strangerTypeA, _ownTypeB, _ownUntyped], ignoreOrder: true);
    }

    [Fact]
    public async Task An_owner_only_scope_translates_to_the_callers_own_rows_typed_or_not()
    {
        await SeedAsync();

        var ids = await QueryAsync(Scope(Owner));

        ids.ShouldBe([_ownTypeA, _ownTypeB, _ownUntyped], ignoreOrder: true);
    }

    /// <summary>
    /// Neither arm — a machine identity with no <c>sub</c> and no grants. The constant-false predicate the scope
    /// builds has to translate too, rather than throwing on the provider.
    /// </summary>
    [Fact]
    public async Task A_scope_with_neither_arm_translates_to_a_predicate_that_returns_nothing()
    {
        await SeedAsync();

        (await QueryAsync(Scope(ownerId: null))).ShouldBeEmpty();
    }

    /// <summary>
    /// The predicate travels through the shared <c>ApplyMetadataFilter</c> chain in production, not on its own,
    /// so the composition has to translate as well: scope AND another predicate, in one SQL statement.
    /// </summary>
    [Fact]
    public async Task The_scope_composes_with_the_shared_metadata_chain_on_the_real_provider()
    {
        await SeedAsync();

        var ids = await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.GetQueryableAsync();
            return query
                .ApplyMetadataFilter(new DocumentMetadataFilter
                {
                    DocumentTypeId = _typeB,
                    ReadScope = Scope(Owner, _typeA)
                })
                .Select(d => d.Id)
                .ToList();
        });

        // Type B is outside the granted set, so only the owner arm can supply a row of it.
        ids.ShouldBe([_ownTypeB]);
    }

    private static DocumentAccessScope Scope(Guid? ownerId, params Guid[] documentTypeIds)
        => DocumentAccessScope.Of(documentTypeIds.ToHashSet(), ownerId);

    private Task<List<Guid>> QueryAsync(DocumentAccessScope scope)
        => WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.GetQueryableAsync();
            return query.Where(scope.ToPredicate()).Select(d => d.Id).ToList();
        });

    private async Task SeedAsync()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var typeRepository = GetRequiredService<IDocumentTypeRepository>();
            _typeA = (await typeRepository.InsertAsync(
                new DocumentType(Guid.NewGuid(), null, "scope.a", "Scope A"), autoSave: true)).Id;
            _typeB = (await typeRepository.InsertAsync(
                new DocumentType(Guid.NewGuid(), null, "scope.b", "Scope B"), autoSave: true)).Id;

            _ownTypeA = await InsertAsync(_typeA, Owner);
            _strangerTypeA = await InsertAsync(_typeA, Stranger);
            _ownTypeB = await InsertAsync(_typeB, Owner);
            _ownUntyped = await InsertAsync(null, Owner);
            _strangerUntyped = await InsertAsync(null, creatorId: null);
        });
    }

    private async Task<Guid> InsertAsync(Guid? documentTypeId, Guid? creatorId)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "scope",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "scope.pdf"));

        if (documentTypeId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId.Value);
        }

        // Set explicitly rather than through a principal switch: ABP's AuditPropertySetter returns early when
        // CreatorId already has a value, which DerivedDocumentOwnership_Tests pins as its own fact.
        if (creatorId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
        }

        await _documentRepository.InsertAsync(document, autoSave: true);
        return document.Id;
    }
}
