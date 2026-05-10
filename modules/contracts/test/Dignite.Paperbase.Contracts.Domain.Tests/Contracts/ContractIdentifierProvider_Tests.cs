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

namespace Dignite.Paperbase.Contracts.Contracts;

[DependsOn(typeof(ContractsDomainTestModule))]
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
    : ContractsDomainTestBase<ContractIdentifierProviderTestModule>
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
    public void SupportedIdentifierTypes_Should_Include_ContractNumber_And_PartyName()
    {
        _provider.SupportedIdentifierTypes.ShouldContain(DocumentIdentifierTypes.ContractNumber);
        _provider.SupportedIdentifierTypes.ShouldContain(DocumentIdentifierTypes.PartyName);
        // Invoice number / PO number / project code are not the contract module's responsibility.
        _provider.SupportedIdentifierTypes.ShouldNotContain(DocumentIdentifierTypes.InvoiceNumber);
        _provider.SupportedIdentifierTypes.ShouldNotContain(DocumentIdentifierTypes.PoNumber);
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
    public async Task GetIdentifiersAsync_Should_Map_Contract_Fields_To_Identifier_Entries()
    {
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            contractNumber: "HT-2026-001",
            partyA: "甲方公司",
            partyB: "乙方公司",
            counterparty: "对手方公司");
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var entries = (await _provider.GetIdentifiersAsync(documentId)).ToList();

        entries.ShouldContain(new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-2026-001"));
        entries.ShouldContain(new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "甲方公司"));
        entries.ShouldContain(new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "乙方公司"));
        entries.ShouldContain(new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "对手方公司"));
    }

    [Fact]
    public async Task GetIdentifiersAsync_Should_Skip_Null_And_Whitespace_Fields()
    {
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            contractNumber: null,
            partyA: "  ",
            partyB: "乙方公司",
            counterparty: null);
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var entries = (await _provider.GetIdentifiersAsync(documentId)).ToList();

        entries.Count.ShouldBe(1);
        entries.Single().ShouldBe(new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "乙方公司"));
    }

    [Fact]
    public async Task GetIdentifiersAsync_Should_Trim_Field_Values()
    {
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            contractNumber: "  HT-2026-002  ",
            partyA: " 甲方 ",
            partyB: null,
            counterparty: null);
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var entries = (await _provider.GetIdentifiersAsync(documentId)).ToList();

        entries.ShouldContain(new DocumentIdentifierEntry(DocumentIdentifierTypes.ContractNumber, "HT-2026-002"));
        entries.ShouldContain(new DocumentIdentifierEntry(DocumentIdentifierTypes.PartyName, "甲方"));
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
        await _contractRepository.DidNotReceive().GetListByPartyNameAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDocumentsAsync_ContractNumber_Should_Trim_And_Delegate()
    {
        var matchingDocId = Guid.NewGuid();
        var matchingContract = CreateContract(matchingDocId, contractNumber: "HT-2026-003");
        _contractRepository
            .FindByContractNumberAsync("HT-2026-003", Arg.Any<CancellationToken>())
            .Returns(new List<Contract> { matchingContract });

        var result = await _provider.FindDocumentsAsync(
            DocumentIdentifierTypes.ContractNumber,
            "  HT-2026-003 ");

        result.ShouldContain(matchingDocId);
    }

    [Fact]
    public async Task FindDocumentsAsync_PartyName_Should_Delegate_To_GetListByPartyName()
    {
        var docA = Guid.NewGuid();
        var docB = Guid.NewGuid();
        _contractRepository
            .GetListByPartyNameAsync("甲方公司", Arg.Any<CancellationToken>())
            .Returns(new List<Contract>
            {
                CreateContract(docA, partyA: "甲方公司"),
                CreateContract(docB, partyB: "甲方公司")
            });

        var result = await _provider.FindDocumentsAsync(
            DocumentIdentifierTypes.PartyName,
            "甲方公司");

        result.ShouldContain(docA);
        result.ShouldContain(docB);
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Distinct_Document_Ids()
    {
        var sharedDoc = Guid.NewGuid();
        // Two Contract rows pointing to the same document (data anomaly we want to defend against).
        _contractRepository
            .FindByContractNumberAsync("HT-2026-004", Arg.Any<CancellationToken>())
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
        string? partyB = null,
        string? counterparty = null)
    {
        var fields = new ContractFields
        {
            ContractNumber = contractNumber,
            PartyAName = partyA,
            PartyBName = partyB,
            CounterpartyName = counterparty,
        };

        // ContractManager wraps the internal Contract constructor. Use it from tests
        // to honor the same construction path the production code uses.
        return _contractManager.CreateAsync(documentId, ContractsDocumentTypes.General, fields)
            .GetAwaiter().GetResult();
    }
}
