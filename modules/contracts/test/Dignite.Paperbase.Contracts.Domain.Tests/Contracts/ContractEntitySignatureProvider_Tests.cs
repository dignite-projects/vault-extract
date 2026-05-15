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
public class ContractEntitySignatureProviderTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Mock the repository — provider correctness is what we exercise here, not EF.
        context.Services.AddSingleton(Substitute.For<IContractRepository>());
    }
}

/// <summary>
/// 硬伤二 (L2 Phase 3) regression suite for the contract module's signature provider.
/// Verifies: signature only fires when all three fields populated; the three field-missing
/// shapes silently skip; lookup canonicalizes through repository's PartiesAndYear query.
/// </summary>
public class ContractEntitySignatureProvider_Tests
    : PaperbaseContractsDomainTestBase<ContractEntitySignatureProviderTestModule>
{
    private readonly ContractEntitySignatureProvider _provider;
    private readonly IContractRepository _contractRepository;
    private readonly ContractManager _contractManager;

    public ContractEntitySignatureProvider_Tests()
    {
        _provider = GetRequiredService<ContractEntitySignatureProvider>();
        _contractRepository = GetRequiredService<IContractRepository>();
        _contractManager = GetRequiredService<ContractManager>();
    }

    [Fact]
    public void Supports_Only_PartiesAndYear_Kind()
    {
        _provider.SupportedSignatureKinds.ShouldContain(ContractEntitySignatureProvider.PartiesAndYearSignatureKind);
        _provider.SupportedSignatureKinds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetSignatures_Should_Return_Empty_When_Contract_Missing()
    {
        var documentId = Guid.NewGuid();
        _contractRepository.FindByDocumentIdAsync(documentId).Returns((Contract?)null);

        var result = await _provider.GetSignaturesAsync(documentId);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSignatures_Should_Emit_When_All_Fields_Populated()
    {
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            partyA: "上海某某科技有限公司",
            partyB: "北京贝塔信息技术有限公司",
            signedDate: new DateTime(2024, 3, 15));
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        var sigs = await _provider.GetSignaturesAsync(documentId);

        var sig = sigs.ShouldHaveSingleItem();
        sig.Kind.ShouldBe(ContractEntitySignatureProvider.PartiesAndYearSignatureKind);
        sig.Fields[ContractEntitySignatureProvider.FieldPartyA].ShouldBe("上海某某科技有限公司");
        sig.Fields[ContractEntitySignatureProvider.FieldPartyB].ShouldBe("北京贝塔信息技术有限公司");
        sig.Fields[ContractEntitySignatureProvider.FieldYear].ShouldBe("2024");
    }

    [Fact]
    public async Task GetSignatures_Should_Skip_When_Any_Party_Missing()
    {
        // 硬伤二 contract: incomplete signatures MUST not be emitted — would otherwise
        // collide noisily with every other "PartyA=null, PartyB=X" combination across docs.
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            partyA: "上海某某",
            partyB: null,                                                 // missing
            signedDate: new DateTime(2024, 3, 15));
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        (await _provider.GetSignaturesAsync(documentId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSignatures_Should_Skip_When_SignedDate_Missing()
    {
        var documentId = Guid.NewGuid();
        var contract = CreateContract(
            documentId,
            partyA: "上海某某",
            partyB: "北京贝塔",
            signedDate: null);                                            // missing
        _contractRepository.FindByDocumentIdAsync(documentId).Returns(contract);

        (await _provider.GetSignaturesAsync(documentId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task FindDocumentsBySignature_Should_Reject_Other_Kinds()
    {
        // Defensive: provider must only respond to its declared SupportedSignatureKinds.
        var foreignSig = new DocumentEntitySignature(
            "Invoices.VendorAndDate",
            new Dictionary<string, string>
            {
                [ContractEntitySignatureProvider.FieldPartyA] = "x",
                [ContractEntitySignatureProvider.FieldPartyB] = "y",
                [ContractEntitySignatureProvider.FieldYear] = "2024",
            });

        (await _provider.FindDocumentsBySignatureAsync(foreignSig)).ShouldBeEmpty();
        // Repository must NOT have been called.
        await _contractRepository.DidNotReceive().FindByPartiesAndYearAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDocumentsBySignature_Should_Reject_Malformed_Year()
    {
        var sig = new DocumentEntitySignature(
            ContractEntitySignatureProvider.PartiesAndYearSignatureKind,
            new Dictionary<string, string>
            {
                [ContractEntitySignatureProvider.FieldPartyA] = "x",
                [ContractEntitySignatureProvider.FieldPartyB] = "y",
                [ContractEntitySignatureProvider.FieldYear] = "abc",  // not parseable
            });

        (await _provider.FindDocumentsBySignatureAsync(sig)).ShouldBeEmpty();
    }

    [Fact]
    public async Task FindDocumentsBySignature_Should_Delegate_And_Map_Document_Ids()
    {
        var docId = Guid.NewGuid();
        var contract = CreateContract(
            docId,
            partyA: "上海某某科技有限公司",
            partyB: "北京贝塔信息技术有限公司",
            signedDate: new DateTime(2024, 3, 15));
        _contractRepository.FindByPartiesAndYearAsync(
                "上海某某科技有限公司",
                "北京贝塔信息技术有限公司",
                2024,
                Arg.Any<CancellationToken>())
            .Returns(new List<Contract> { contract });

        var sig = new DocumentEntitySignature(
            ContractEntitySignatureProvider.PartiesAndYearSignatureKind,
            new Dictionary<string, string>
            {
                [ContractEntitySignatureProvider.FieldPartyA] = "上海某某科技有限公司",
                [ContractEntitySignatureProvider.FieldPartyB] = "北京贝塔信息技术有限公司",
                [ContractEntitySignatureProvider.FieldYear] = "2024",
            });

        var result = await _provider.FindDocumentsBySignatureAsync(sig);

        result.ShouldContain(docId);
    }

    private Contract CreateContract(
        Guid documentId,
        string? contractNumber = null,
        string? partyA = null,
        string? partyB = null,
        DateTime? signedDate = null)
    {
        var fields = new ContractFields
        {
            ContractNumber = contractNumber,
            PartyAName = partyA,
            PartyBName = partyB,
            SignedDate = signedDate,
        };

        return _contractManager.CreateAsync(documentId, PaperbaseContractsDocumentTypes.General, fields)
            .GetAwaiter().GetResult();
    }
}
