using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class RelationDiscoveryServiceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Repository is mocked — RelationDiscoveryService writes via InsertAsync (autoSave: false),
        // so we verify the entity it constructed without exercising EF.
        context.Services.AddSingleton(Substitute.For<IDocumentRelationRepository>());

        // Issue #121: L2 now loads source Document for tenant id (Hangfire-safe). Mock the
        // document repository; tests configure FindAsync per-case via SetupSource.
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());

        // Two providers wired into DI — singletons so the test class instance and the service's
        // injected IEnumerable resolve to the SAME fake instances (otherwise transient resolution
        // gives the service a different enumeration than what the test mutates → state silently lost).
        // AbpIntegratedTest constructs a fresh container per test class instance, so singletons
        // don't leak across tests.
        context.Services.AddSingleton<FakeContractProvider>();
        context.Services.AddSingleton<FakeInvoiceProvider>();
        context.Services.AddSingleton<IDocumentIdentifierProvider>(sp => sp.GetRequiredService<FakeContractProvider>());
        context.Services.AddSingleton<IDocumentIdentifierProvider>(sp => sp.GetRequiredService<FakeInvoiceProvider>());
    }
}

public class RelationDiscoveryService_Tests
    : PaperbaseApplicationTestBase<RelationDiscoveryServiceTestModule>
{
    private readonly RelationDiscoveryService _service;
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly FakeContractProvider _contractProvider;
    private readonly FakeInvoiceProvider _invoiceProvider;

    public RelationDiscoveryService_Tests()
    {
        _service = GetRequiredService<RelationDiscoveryService>();
        _relationRepository = GetRequiredService<IDocumentRelationRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        // Resolve the singletons by their concrete type — same instances as those enumerated
        // through IEnumerable<IDocumentIdentifierProvider> by the service.
        _contractProvider = GetRequiredService<FakeContractProvider>();
        _invoiceProvider = GetRequiredService<FakeInvoiceProvider>();

        // Default: any document id resolves to a tenantless Document. Tests that exercise
        // tenant-stamping or "doc not found" override this via SetupSource / SetupSourceMissing.
        _documentRepository
            .FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateDocumentWithId((Guid)callInfo[0], tenantId: null));
    }

    // ── Issue #121 helper: wire FindAsync(documentId) → Document with optional tenant ──
    private void SetupSource(Guid documentId, Guid? tenantId = null)
    {
        var doc = CreateDocumentWithId(documentId, tenantId);
        _documentRepository.FindAsync(documentId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void SetupSourceMissing(Guid documentId)
    {
        _documentRepository.FindAsync(documentId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);
    }

    private static Document CreateDocumentWithId(Guid id, Guid? tenantId)
    {
        return new Document(
            id, tenantId,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_No_Provider_Has_Identifiers_For_Source()
    {
        var sourceDocId = Guid.NewGuid();
        // Both providers are empty — no identifiers known for this document.

        var created = await _service.DiscoverAsync(sourceDocId);

        created.ShouldBeEmpty();
        await _relationRepository.DidNotReceive().InsertAsync(
            Arg.Any<DocumentRelation>(), autoSave: Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Reject_Empty_Document_Id()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await _service.DiscoverAsync(Guid.Empty));
    }

    [Fact]
    public async Task DiscoverAsync_Should_Drop_Silently_When_Source_Document_Hard_Deleted()
    {
        // Issue #121: source loaded for tenant resolution. If document was hard-deleted between
        // event publish and L2 execution → drop without throwing (matches handler / job pattern).
        var sourceDocId = Guid.NewGuid();
        SetupSourceMissing(sourceDocId);

        var created = await _service.DiscoverAsync(sourceDocId);

        created.ShouldBeEmpty();
        // Providers must not be queried — short-circuit on document-not-found is the cheapest path.
        _contractProvider.FindCalls.ShouldBeEmpty();
        _invoiceProvider.FindCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Stamp_New_Relation_With_Source_Document_TenantId()
    {
        // Issue #121: TenantId on the new DocumentRelation must come from Document.TenantId,
        // NOT from ambient ICurrentTenant (Hangfire-safe). Test asserts a non-null source
        // tenant ID propagates correctly without ICurrentTenant.Change(...).
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();
        var sourceTenantId = Guid.NewGuid();

        SetupSource(sourceDocId, tenantId: sourceTenantId);

        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-TENANT")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-TENANT")] = new[] { peerDocId };
        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        created.Single().TenantId.ShouldBe(sourceTenantId);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Run_Provider_Calls_Without_Ambient_UoW()
    {
        // .claude/rules/background-jobs.md § Tests: regression guard at the provider call boundary.
        // L2 itself only does DB queries, but providers are pluggable (third-party modules may
        // do HTTP / cache / LLM in future iterations) — we must not open an ambient UoW around them.
        // OnGetIdentifiersInvoked / OnFindDocumentsInvoked callbacks fire INSIDE provider methods,
        // so the assertion captures UoW state at exactly the boundary the rule cares about.
        var uowManager = GetRequiredService<Volo.Abp.Uow.IUnitOfWorkManager>();
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();

        var getIdentifiersUowState = (object?)"unset";
        var findDocumentsUowState = (object?)"unset";

        _contractProvider.OnGetIdentifiersInvoked = () => getIdentifiersUowState = uowManager.Current;
        _contractProvider.OnFindDocumentsInvoked = () => findDocumentsUowState = uowManager.Current;

        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-UOW-CHECK")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-UOW-CHECK")] = new[] { peerDocId };
        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        await _service.DiscoverAsync(sourceDocId);

        getIdentifiersUowState.ShouldBeNull("provider GetIdentifiersAsync ran with ambient UoW");
        findDocumentsUowState.ShouldBeNull("provider FindDocumentsAsync ran with ambient UoW");
    }

    [Fact]
    public async Task DiscoverAsync_Should_Create_AiSuggested_Relation_For_Each_Cross_Module_Peer()
    {
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();

        // Source is a contract holding ContractNumber=HT-001.
        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-001")
        };
        // Invoice peer also holds ContractNumber=HT-001 → cross-module match expected.
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-001")] = new[] { peerDocId };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        var relation = created.Single();
        relation.SourceDocumentId.ShouldBe(sourceDocId);
        relation.TargetDocumentId.ShouldBe(peerDocId);
        relation.Source.ShouldBe(RelationSource.AiSuggested);
        relation.Confidence.ShouldBe(RelationDiscoveryService.StructuralMatchConfidence);
        relation.Description.ShouldContain("ContractNumber");
        relation.Description.ShouldContain("HT-001");
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Peers_That_Already_Have_A_Relation()
    {
        var sourceDocId = Guid.NewGuid();
        var alreadyLinkedPeerId = Guid.NewGuid();
        var freshPeerId = Guid.NewGuid();

        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-002")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-002")] = new[] { alreadyLinkedPeerId, freshPeerId };

        // alreadyLinkedPeerId is already related (Manual — user-confirmed) — must NOT be re-suggested.
        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>
            {
                CreateExistingRelation(sourceDocId, alreadyLinkedPeerId, RelationSource.Manual)
            });

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        created.Single().TargetDocumentId.ShouldBe(freshPeerId);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Create_Only_One_Relation_When_Same_Peer_Matches_Multiple_Identifiers()
    {
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();

        // Source has two identifiers; both happen to point to the same peer document.
        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-003"),
            new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "上海某某有限公司"),
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-003")] = new[] { peerDocId };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.PartyName, "上海某某有限公司")] = new[] { peerDocId };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        // Description reflects the FIRST identifier that found this peer — first-write-wins.
        created.Single().Description.ShouldContain("ContractNumber");
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Self_When_Provider_Returns_Source_Document_Id()
    {
        var sourceDocId = Guid.NewGuid();
        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "甲方公司")
        };
        // Defensive: provider FindDocumentsAsync echoes source itself (because contract module
        // also holds the source document). Service must filter self.
        _contractProvider.Lookup[(DocumentIdentifierTypes.PartyName, "甲方公司")] = new[] { sourceDocId };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Peers_Already_Linked_Via_AiSuggested()
    {
        // Symmetric to the Manual-skip test: AiSuggested relations are also "existing"
        // and L2 must not duplicate them on re-runs (idempotency).
        var sourceDocId = Guid.NewGuid();
        var alreadyAiLinkedPeer = Guid.NewGuid();

        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-IDEMPOTENT")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-IDEMPOTENT")]
            = new[] { alreadyAiLinkedPeer };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>
            {
                CreateExistingRelation(sourceDocId, alreadyAiLinkedPeer, RelationSource.AiSuggested)
            });

        var created = await _service.DiscoverAsync(sourceDocId);

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Continue_When_One_Provider_Throws_In_GetIdentifiers()
    {
        // Provider isolation: a buggy module must not tank L2 for the whole document.
        // Contract provider throws; invoice provider's contribution should still go through.
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();

        _contractProvider.GetIdentifiersThrowsFor.Add(sourceDocId);
        _invoiceProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.InvoiceNumber, "INV-RESILIENT")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.InvoiceNumber, "INV-RESILIENT")] = new[] { peerDocId };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        created.Single().TargetDocumentId.ShouldBe(peerDocId);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Continue_When_One_Provider_Throws_In_FindDocuments()
    {
        // Same isolation contract on the reverse-lookup side.
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();

        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-RESILIENT")
        };
        _contractProvider.FindDocumentsThrowsFor.Add((DocumentIdentifierTypes.ContractNumber, "HT-RESILIENT"));
        // Invoice provider succeeds for the same identifier — peer should still surface.
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-RESILIENT")] = new[] { peerDocId };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        created.Single().TargetDocumentId.ShouldBe(peerDocId);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Providers_That_Do_Not_Support_Identifier_Type()
    {
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();

        // Source has an InvoiceNumber identifier — only the invoice provider supports it.
        // The contract provider does NOT support InvoiceNumber, so its FindDocumentsAsync
        // must not be called for this type.
        _invoiceProvider.Identifiers[sourceDocId] = new[]
        {
            new DocumentIdentifierEntry(DocumentIdentifierTypes.InvoiceNumber, "INV-001")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.InvoiceNumber, "INV-001")] = new[] { peerDocId };

        _relationRepository.GetListByDocumentIdAsync(sourceDocId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        // Verify contract provider's FindDocumentsAsync was NOT called for InvoiceNumber.
        _contractProvider.FindCalls.ShouldNotContain(c => c.Type == DocumentIdentifierTypes.InvoiceNumber);
    }

    private static DocumentRelation CreateExistingRelation(Guid source, Guid target, RelationSource src)
    {
        return new DocumentRelation(
            Guid.NewGuid(),
            tenantId: null,
            sourceDocumentId: source,
            targetDocumentId: target,
            description: "Manual link",
            source: src,
            confidence: src == RelationSource.Manual ? null : 0.9);
    }
}

// ─── Test doubles ──────────────────────────────────────────────────────────────

internal sealed class FakeContractProvider : IDocumentIdentifierProvider
{
    public IReadOnlyCollection<string> SupportedIdentifierTypes { get; } = new[]
    {
        DocumentIdentifierTypes.ContractNumber,
        DocumentIdentifierTypes.PartyName,
    };

    public Dictionary<Guid, IReadOnlyList<DocumentIdentifierEntry>> Identifiers { get; } = new();
    public Dictionary<(string Type, string Value), IReadOnlyList<Guid>> Lookup { get; } = new();
    public List<(string Type, string Value)> FindCalls { get; } = new();
    // Failure-injection knobs used by provider-isolation tests.
    public HashSet<Guid> GetIdentifiersThrowsFor { get; } = new();
    public HashSet<(string Type, string Value)> FindDocumentsThrowsFor { get; } = new();
    // Inspection hook used by UoW-null-assertion tests; runs at the provider call boundary.
    public Action? OnGetIdentifiersInvoked { get; set; }
    public Action? OnFindDocumentsInvoked { get; set; }

    public Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        OnGetIdentifiersInvoked?.Invoke();
        if (GetIdentifiersThrowsFor.Contains(documentId))
            throw new InvalidOperationException("Simulated provider failure (GetIdentifiersAsync)");
        return Task.FromResult(Identifiers.TryGetValue(documentId, out var v) ? v : (IReadOnlyList<DocumentIdentifierEntry>)Array.Empty<DocumentIdentifierEntry>());
    }

    public Task<IReadOnlyList<Guid>> FindDocumentsAsync(string identifierType, string identifierValue, CancellationToken cancellationToken = default)
    {
        OnFindDocumentsInvoked?.Invoke();
        FindCalls.Add((identifierType, identifierValue));
        if (FindDocumentsThrowsFor.Contains((identifierType, identifierValue)))
            throw new InvalidOperationException("Simulated provider failure (FindDocumentsAsync)");
        return Task.FromResult(Lookup.TryGetValue((identifierType, identifierValue), out var v) ? v : (IReadOnlyList<Guid>)Array.Empty<Guid>());
    }
}

internal sealed class FakeInvoiceProvider : IDocumentIdentifierProvider
{
    public IReadOnlyCollection<string> SupportedIdentifierTypes { get; } = new[]
    {
        DocumentIdentifierTypes.ContractNumber,
        DocumentIdentifierTypes.PartyName,
        DocumentIdentifierTypes.InvoiceNumber,
    };

    public Dictionary<Guid, IReadOnlyList<DocumentIdentifierEntry>> Identifiers { get; } = new();
    public Dictionary<(string Type, string Value), IReadOnlyList<Guid>> Lookup { get; } = new();
    public List<(string Type, string Value)> FindCalls { get; } = new();

    public Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(Guid documentId, CancellationToken cancellationToken = default)
        => Task.FromResult(Identifiers.TryGetValue(documentId, out var v) ? v : (IReadOnlyList<DocumentIdentifierEntry>)Array.Empty<DocumentIdentifierEntry>());

    public Task<IReadOnlyList<Guid>> FindDocumentsAsync(string identifierType, string identifierValue, CancellationToken cancellationToken = default)
    {
        FindCalls.Add((identifierType, identifierValue));
        return Task.FromResult(Lookup.TryGetValue((identifierType, identifierValue), out var v) ? v : (IReadOnlyList<Guid>)Array.Empty<Guid>());
    }
}
