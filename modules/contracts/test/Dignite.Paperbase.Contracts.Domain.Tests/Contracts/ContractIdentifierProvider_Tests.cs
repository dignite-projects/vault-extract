using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Contracts;

[DependsOn(typeof(PaperbaseContractsDomainTestModule))]
public class ContractIdentifierProviderTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Mock the repository to avoid spinning up an EF context for behavioral tests.
        // Repository implementation correctness is covered by EFCore.Tests when needed.
        context.Services.AddSingleton(Substitute.For<IContractRepository>());
    }
}

public class ContractIdentifierProvider_Tests
    : PaperbaseContractsDomainTestBase<ContractIdentifierProviderTestModule>
{
    private readonly ContractIdentifierProvider _provider;
    private readonly IContractRepository _contractRepository;
    private readonly ContractManager _contractManager;

    public ContractIdentifierProvider_Tests()
    {
        _provider = GetRequiredService<ContractIdentifierProvider>();
        _contractRepository = GetRequiredService<IContractRepository>();
        _contractManager = GetRequiredService<ContractManager>();
    }

    [Fact]
    public void SupportedIdentifierTypes_Should_Be_Only_ContractNumber()
    {
        // Codex review fix [high]: PartyName is INTENTIONALLY excluded — using common
        // counterparty / vendor names as L2 structural identifiers creates false high-confidence
        // graphs. Party-based relations are L3's responsibility (LLM judgment with context).
        _provider.SupportedIdentifierTypes.ShouldContain(DocumentIdentifierTypes.ContractNumber);
        _provider.SupportedIdentifierTypes.ShouldNotContain(DocumentIdentifierTypes.PartyName);
        _provider.SupportedIdentifierTypes.ShouldNotContain(DocumentIdentifierTypes.InvoiceNumber);
        _provider.SupportedIdentifierTypes.ShouldNotContain(DocumentIdentifierTypes.PoNumber);
        _provider.SupportedIdentifierTypes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetIdentifiersAsync_Should_Return_Empty_When_Document_Not_Owned_By_Contracts_Module()
    {
        var documentId = Guid.NewGuid();
        _contractRepository.FindByDocumentIdAsync(documentId).Returns((Contract?)null);

        var entries = await _provider.GetIdentifiersAsync(documentId);

        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetIdentifiersAsync_Should_Emit_Only_ContractNumber_When_Present()
    {
        // Codex review fix [high]: PartyAName / PartyBName are INTENTIONALLY not emitted
        // as L2 identifiers (graph blow-up risk).
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            contractNumber: "HT-2026-001",
            partyA: "甲方公司",
            partyB: "乙方公司");
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var entries = (await _provider.GetIdentifiersAsync(documentId)).ToList();

        entries.Count.ShouldBe(1);
        entries.Single().ShouldBe(new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-2026-001"));
        // Explicitly assert PartyName values are NOT in the output, even though they're
        // present on the Contract aggregate.
        entries.ShouldNotContain(e => e.Type == DocumentIdentifierTypes.PartyName);
    }

    [Fact]
    public async Task GetIdentifiersAsync_Should_Return_Empty_When_ContractNumber_Is_Blank()
    {
        // With PartyName dropped, a contract without a ContractNumber contributes nothing to L2.
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            contractNumber: null,
            partyA: "甲方公司",       // Has parties but no number → no L2 contribution.
            partyB: "乙方公司");
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var entries = (await _provider.GetIdentifiersAsync(documentId)).ToList();

        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetIdentifiersAsync_Should_Trim_ContractNumber()
    {
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            contractNumber: "  HT-2026-002  ",
            partyA: " 甲方 ",
            partyB: null);
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var entries = (await _provider.GetIdentifiersAsync(documentId)).ToList();

        entries.Count.ShouldBe(1);
        entries.Single().ShouldBe(new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-2026-002"));
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Return_Empty_For_Whitespace_Value()
    {
        var result = await _provider.FindDocumentsAsync(DocumentIdentifierTypes.ContractNumber, "  ");
        result.ShouldBeEmpty();

        await _contractRepository.DidNotReceive().FindByContractNumberAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Return_Empty_For_Unsupported_Type()
    {
        var result = await _provider.FindDocumentsAsync(DocumentIdentifierTypes.InvoiceNumber, "INV-001");
        result.ShouldBeEmpty();

        // Defensive: unsupported types short-circuit before any repository call.
        await _contractRepository.DidNotReceive().FindByContractNumberAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Return_Empty_For_PartyName()
    {
        // Codex review fix [high]: PartyName is no longer a supported L2 identifier.
        // Even if a caller (defensively or from a test) passes it in, the provider must
        // return empty without hitting the repository.
        var result = await _provider.FindDocumentsAsync(DocumentIdentifierTypes.PartyName, "甲方公司");
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindDocumentsAsync_ContractNumber_Should_Normalize_And_Delegate()
    {
        // 硬伤一 Phase 1: provider normalizes the lookup value before hitting the repo.
        // The repo is expected to query the indexed NormalizedContractNumber column, so the
        // stub is keyed on the normalized form "HT2026003" — and the caller sends the raw
        // form (with surrounding whitespace and dashes), which the provider strips.
        var matchingDocId = Guid.NewGuid();
        var matchingContract = CreateContract(matchingDocId, contractNumber: "HT-2026-003");
        _contractRepository
            .FindByContractNumberAsync("HT2026003", Arg.Any<CancellationToken>())
            .Returns(new List<Contract> { matchingContract });

        var result = await _provider.FindDocumentsAsync(
            DocumentIdentifierTypes.ContractNumber,
            "  HT-2026-003 ");

        result.ShouldContain(matchingDocId);
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Distinct_Document_Ids()
    {
        var sharedDoc = Guid.NewGuid();
        // Two Contract rows pointing to the same document (data anomaly we want to defend against).
        // Stub keyed on normalized form (硬伤一 Phase 1).
        _contractRepository
            .FindByContractNumberAsync("HT2026004", Arg.Any<CancellationToken>())
            .Returns(new List<Contract>
            {
                CreateContract(sharedDoc, contractNumber: "HT-2026-004"),
                CreateContract(sharedDoc, contractNumber: "HT-2026-004"),
            });

        var result = await _provider.FindDocumentsAsync(
            DocumentIdentifierTypes.ContractNumber,
            "HT-2026-004");

        result.Count.ShouldBe(1);
    }

    private Contract CreateContract(
        Guid documentId,
        string? contractNumber = null,
        string? partyA = null,
        string? partyB = null)
    {
        var fields = new ContractFields
        {
            ContractNumber = contractNumber,
            PartyAName = partyA,
            PartyBName = partyB,
        };

        // ContractManager wraps the internal Contract constructor. Use it from tests
        // to honor the same construction path the production code uses.
        return _contractManager.CreateAsync(documentId, PaperbaseContractsDocumentTypes.General, fields)
            .GetAwaiter().GetResult();
    }
}
