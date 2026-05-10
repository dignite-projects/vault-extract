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
    private readonly FakeContractProvider _contractProvider;
    private readonly FakeInvoiceProvider _invoiceProvider;

    public RelationDiscoveryService_Tests()
    {
        _service = GetRequiredService<RelationDiscoveryService>();
        _relationRepository = GetRequiredService<IDocumentRelationRepository>();
        // Resolve the singletons by their concrete type — same instances as those enumerated
        // through IEnumerable<IDocumentIdentifierProvider> by the service.
        _contractProvider = GetRequiredService<FakeContractProvider>();
        _invoiceProvider = GetRequiredService<FakeInvoiceProvider>();
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

    public Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (GetIdentifiersThrowsFor.Contains(documentId))
            throw new InvalidOperationException("Simulated provider failure (GetIdentifiersAsync)");
        return Task.FromResult(Identifiers.TryGetValue(documentId, out var v) ? v : (IReadOnlyList<DocumentIdentifierEntry>)Array.Empty<DocumentIdentifierEntry>());
    }

    public Task<IReadOnlyList<Guid>> FindDocumentsAsync(string identifierType, string identifierValue, CancellationToken cancellationToken = default)
    {
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
