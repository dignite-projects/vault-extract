using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        // 硬伤二 (L2 Phase 3): fake signature provider exercising multi-field entity-signature
        // fan-out alongside the identifier path.
        context.Services.AddSingleton<FakeSignatureProvider>();
        context.Services.AddSingleton<IDocumentEntitySignatureProvider>(sp => sp.GetRequiredService<FakeSignatureProvider>());

        // 硬伤三 substitute — tests can verify which telemetry events L2 emitted (per-provider
        // contribution, orphan documents, high-ambiguity warnings).
        context.Services.AddSingleton(Substitute.For<RelationDiscoveryTelemetryRecorder>(
            NullLogger<RelationDiscoveryTelemetryRecorder>.Instance));
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
    private readonly FakeSignatureProvider _signatureProvider;
    private readonly RelationDiscoveryTelemetryRecorder _telemetry;

    public RelationDiscoveryService_Tests()
    {
        _service = GetRequiredService<RelationDiscoveryService>();
        _relationRepository = GetRequiredService<IDocumentRelationRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        // Resolve the singletons by their concrete type — same instances as those enumerated
        // through IEnumerable<IDocumentIdentifierProvider> by the service.
        _contractProvider = GetRequiredService<FakeContractProvider>();
        _invoiceProvider = GetRequiredService<FakeInvoiceProvider>();
        _signatureProvider = GetRequiredService<FakeSignatureProvider>();
        _telemetry = GetRequiredService<RelationDiscoveryTelemetryRecorder>();

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-TENANT")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-TENANT")] = new[] { peerDocId };
        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-UOW-CHECK")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-UOW-CHECK")] = new[] { peerDocId };
        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-001")
        };
        // Invoice peer also holds ContractNumber=HT-001 → cross-module match expected.
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-001")] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-002")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-002")] = new[] { alreadyLinkedPeerId, freshPeerId };

        // alreadyLinkedPeerId is already related (Manual — user-confirmed) — must NOT be re-suggested.
        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { alreadyLinkedPeerId });

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-003"),
            NameEntry(DocumentIdentifierTypes.PartyName, "上海某某有限公司"),
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-003")] = new[] { peerDocId };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.PartyName, "上海某某有限公司")] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            NameEntry(DocumentIdentifierTypes.PartyName, "甲方公司")
        };
        // Defensive: provider FindDocumentsAsync echoes source itself (because contract module
        // also holds the source document). Service must filter self.
        _contractProvider.Lookup[(DocumentIdentifierTypes.PartyName, "甲方公司")] = new[] { sourceDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-IDEMPOTENT")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-IDEMPOTENT")]
            = new[] { alreadyAiLinkedPeer };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { alreadyAiLinkedPeer });

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
            CodeEntry(DocumentIdentifierTypes.InvoiceNumber, "INV-RESILIENT")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.InvoiceNumber, "INV-RESILIENT")] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-RESILIENT")
        };
        _contractProvider.FindDocumentsThrowsFor.Add((DocumentIdentifierTypes.ContractNumber, "HT-RESILIENT"));
        // Invoice provider succeeds for the same identifier — peer should still surface.
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-RESILIENT")] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

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
            CodeEntry(DocumentIdentifierTypes.InvoiceNumber, "INV-001")
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.InvoiceNumber, "INV-001")] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var created = await _service.DiscoverAsync(sourceDocId);

        created.Count.ShouldBe(1);
        // Verify contract provider's FindDocumentsAsync was NOT called for InvoiceNumber.
        _contractProvider.FindCalls.ShouldNotContain(c => c.Type == DocumentIdentifierTypes.InvoiceNumber);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Record_Orphan_When_Document_Has_No_Identifiers()
    {
        // 硬伤三 regression guard: a Document arriving at L2 with no identifiers fires the
        // orphan-document telemetry. Operators use this to spot extraction regressions
        // (a module that suddenly stops producing identifiers shows up as an orphan spike).
        var sourceDocId = Guid.NewGuid();
        SetupSource(sourceDocId);
        // No Identifiers / no Lookup entries → no identifiers collected.

        await _service.DiscoverAsync(sourceDocId);

        _telemetry.Received(1).RecordOrphanDocument();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Record_Per_Provider_Identifier_Counts()
    {
        // 硬伤三 regression guard: each provider's contribution is reported separately,
        // even when it produces zero identifiers. Lets dashboards break down "which
        // business module is actually wiring identifiers into L2".
        var sourceDocId = Guid.NewGuid();
        SetupSource(sourceDocId);
        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-2024-007"),
        };
        // Invoice provider not given anything → should report 0 contribution.

        await _service.DiscoverAsync(sourceDocId);

        _telemetry.Received().RecordIdentifiersByProvider(nameof(FakeContractProvider), 1);
        _telemetry.Received().RecordIdentifiersByProvider(nameof(FakeInvoiceProvider), 0);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Discover_Peer_Via_Entity_Signature()
    {
        // 硬伤二 (L2 Phase 3) end-to-end: source has no shared single identifier, but its
        // multi-field signature matches a peer's. L2 must surface the relationship with the
        // signature's inherent confidence (NOT the structural-match 0.95).
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();
        SetupSource(sourceDocId);
        // No identifiers — exercise pure signature path.

        var signature = new DocumentEntitySignature(
            FakeSignatureProvider.TestSignatureKind,
            new Dictionary<string, string>
            {
                ["PartyA"] = "上海某某科技有限公司",
                ["PartyB"] = "北京贝塔信息技术有限公司",
                ["Year"] = "2024",
            },
            InherentConfidence: 0.80);
        _signatureProvider.Signatures[sourceDocId] = new[] { signature };
        _signatureProvider.Lookup[FakeSignatureProvider.FingerprintOf(signature)] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var created = await _service.DiscoverAsync(sourceDocId);

        var relation = created.ShouldHaveSingleItem();
        relation.TargetDocumentId.ShouldBe(peerDocId);
        relation.Source.ShouldBe(RelationSource.AiSuggested);
        relation.Confidence.ShouldBe(0.80);                              // signature's inherent confidence
        relation.Description.ShouldContain("Test.Signature");
        relation.Description.ShouldContain("PartyA=上海某某科技有限公司");
        relation.Description.ShouldContain("Year=2024");
    }

    [Fact]
    public async Task DiscoverAsync_Identifier_Match_Wins_Over_Signature_Match_When_Same_Peer()
    {
        // 硬伤二 dedup contract: if a peer is found via BOTH identifier and signature paths
        // (deterministic and statistical evidence), the identifier path's higher-confidence
        // relation is what gets persisted — single source of truth per (source, peer) pair.
        var sourceDocId = Guid.NewGuid();
        var peerDocId = Guid.NewGuid();
        SetupSource(sourceDocId);

        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "HT-2024-009"),
        };
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "HT-2024-009")] = new[] { peerDocId };

        // Same peer also matches a signature.
        var signature = new DocumentEntitySignature(
            FakeSignatureProvider.TestSignatureKind,
            new Dictionary<string, string> { ["X"] = "y" },
            InherentConfidence: 0.70);
        _signatureProvider.Signatures[sourceDocId] = new[] { signature };
        _signatureProvider.Lookup[FakeSignatureProvider.FingerprintOf(signature)] = new[] { peerDocId };

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var created = await _service.DiscoverAsync(sourceDocId);

        var relation = created.ShouldHaveSingleItem();
        // Confidence is the IDENTIFIER path's 0.95 — not the signature's 0.70.
        relation.Confidence.ShouldBe(RelationDiscoveryService.StructuralMatchConfidence);
        relation.Description.ShouldContain("Identifier match");
        relation.Description.ShouldNotContain("Entity signature");
    }

    [Fact]
    public async Task DiscoverAsync_Should_Record_Orphan_When_Neither_Identifiers_Nor_Signatures()
    {
        // 硬伤二 + 硬伤三 interaction: a source with no identifiers AND no signatures is the
        // true "orphan" — operators see this on the orphan metric. A source with signatures
        // but no identifiers is NOT an orphan (it owns its semantic record).
        var sourceDocId = Guid.NewGuid();
        SetupSource(sourceDocId);
        // No identifiers, no signatures.

        await _service.DiscoverAsync(sourceDocId);

        _telemetry.Received(1).RecordOrphanDocument();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Not_Record_Orphan_When_Only_Signatures_Present()
    {
        var sourceDocId = Guid.NewGuid();
        SetupSource(sourceDocId);
        _signatureProvider.Signatures[sourceDocId] = new[]
        {
            new DocumentEntitySignature(FakeSignatureProvider.TestSignatureKind,
                new Dictionary<string, string> { ["X"] = "y" }, 0.8),
        };

        await _service.DiscoverAsync(sourceDocId);

        _telemetry.DidNotReceive().RecordOrphanDocument();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Flag_High_Ambiguity_Identifier_When_Many_Peers_Match()
    {
        // 硬伤三 regression guard: an identifier that matches more than HighAmbiguityPeerThreshold
        // distinct peers is treated as noise (LLM hallucinated a generic value as a contract
        // number, or the identifier type is over-broad). Telemetry + warning log so operators
        // can spot the type and exclude it from the provider's SupportedIdentifierTypes.
        var sourceDocId = Guid.NewGuid();
        SetupSource(sourceDocId);
        _contractProvider.Identifiers[sourceDocId] = new[]
        {
            CodeEntry(DocumentIdentifierTypes.ContractNumber, "noise-value"),
        };
        var noisyPeers = Enumerable.Range(0, RelationDiscoveryTelemetryRecorder.HighAmbiguityPeerThreshold + 2)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        _invoiceProvider.Lookup[(DocumentIdentifierTypes.ContractNumber, "noise-value")] = noisyPeers;

        _relationRepository.GetLinkedPeerDocumentIdsAsync(
                sourceDocId, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await _service.DiscoverAsync(sourceDocId);

        _telemetry.Received(1).RecordHighAmbiguityIdentifier(
            DocumentIdentifierTypes.ContractNumber,
            Arg.Any<string>(),                 // normalized value — doesn't matter for the assertion
            Arg.Is<int>(n => n >= RelationDiscoveryTelemetryRecorder.HighAmbiguityPeerThreshold));
    }

    /// <summary>
    /// Test helper — produces a <see cref="DocumentIdentifierEntry"/> with the
    /// IdentifierCode normalization (covers ContractNumber / InvoiceNumber / PoNumber /
    /// ProjectCode + any code-shaped module-private type). Tests for name-shaped types
    /// (e.g. PartyName) build entries inline with NormalizeEntityName explicitly.
    /// </summary>
    private static DocumentIdentifierEntry CodeEntry(string type, string raw)
        => new(type, raw, DocumentIdentifierNormalization.NormalizeIdentifierCode(raw));

    private static DocumentIdentifierEntry NameEntry(string type, string raw)
        => new(type, raw, DocumentIdentifierNormalization.NormalizeEntityName(raw));

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

/// <summary>
/// 硬伤二 (L2 Phase 3) test double: a signature provider that L2 fan-outs alongside the
/// single-field identifier providers. Tests register expected signatures via
/// <see cref="Signatures"/> and expected lookup hits via <see cref="Lookup"/>.
/// </summary>
internal sealed class FakeSignatureProvider : IDocumentEntitySignatureProvider
{
    public const string TestSignatureKind = "Test.Signature";

    public IReadOnlyCollection<string> SupportedSignatureKinds { get; } = new[] { TestSignatureKind };

    public Dictionary<Guid, IReadOnlyList<DocumentEntitySignature>> Signatures { get; } = new();

    /// <summary>
    /// Maps a "fields fingerprint" (canonical ordered string of key=value pairs) → peer doc ids.
    /// Tests use <see cref="FingerprintOf"/> to compute the key consistently.
    /// </summary>
    public Dictionary<string, IReadOnlyList<Guid>> Lookup { get; } = new();

    public List<DocumentEntitySignature> FindCalls { get; } = new();

    public Task<IReadOnlyList<DocumentEntitySignature>> GetSignaturesAsync(Guid documentId, CancellationToken cancellationToken = default)
        => Task.FromResult(Signatures.TryGetValue(documentId, out var v) ? v : (IReadOnlyList<DocumentEntitySignature>)Array.Empty<DocumentEntitySignature>());

    public Task<IReadOnlyList<Guid>> FindDocumentsBySignatureAsync(DocumentEntitySignature signature, CancellationToken cancellationToken = default)
    {
        FindCalls.Add(signature);
        var key = FingerprintOf(signature);
        return Task.FromResult(Lookup.TryGetValue(key, out var v) ? v : (IReadOnlyList<Guid>)Array.Empty<Guid>());
    }

    public static string FingerprintOf(DocumentEntitySignature signature)
    {
        var ordered = signature.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal);
        return signature.Kind + "|" + string.Join("|", ordered.Select(kv => kv.Key + "=" + kv.Value));
    }
}

/// <summary>
/// Tests put raw (un-normalized) values in <see cref="Lookup"/>; lookups happen on
/// L2's normalized form. <see cref="LookupNormalized"/> handles both directions so test
/// code can keep using natural raw values ("HT-001", not "HT001") while still exercising
/// the L2 normalization path (硬伤一 Phase 1).
/// </summary>
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
        return Task.FromResult(FakeIdentifierLookupResolver.Resolve(Lookup, identifierType, identifierValue));
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
        return Task.FromResult(FakeIdentifierLookupResolver.Resolve(Lookup, identifierType, identifierValue));
    }
}

/// <summary>
/// Shared resolver — production providers (e.g. <c>ContractIdentifierProvider</c>) normalize
/// the lookup value before calling the repository. Test fakes mirror that behavior so test
/// setups can use natural raw values ("HT-001") while the L2 service sends normalized lookups
/// ("HT001"). Tries exact-key match first (fast path); if missing, scans the dict comparing
/// keys via the same normalization L2 uses.
/// </summary>
internal static class FakeIdentifierLookupResolver
{
    public static IReadOnlyList<Guid> Resolve(
        Dictionary<(string Type, string Value), IReadOnlyList<Guid>> lookup,
        string identifierType,
        string identifierValue)
    {
        if (lookup.TryGetValue((identifierType, identifierValue), out var direct))
        {
            return direct;
        }

        foreach (var entry in lookup)
        {
            if (entry.Key.Type != identifierType) continue;
            // Try both common normalization strategies — the fake doesn't know the type's
            // semantic class, so it accepts either as a match. This is intentional test
            // ergonomics (tests use raw setup values, L2 sends normalized lookups).
            var codeForm = DocumentIdentifierNormalization.NormalizeIdentifierCode(entry.Key.Value);
            var nameForm = DocumentIdentifierNormalization.NormalizeEntityName(entry.Key.Value);
            if (codeForm == identifierValue || nameForm == identifierValue)
            {
                return entry.Value;
            }
        }
        return Array.Empty<Guid>();
    }
}
