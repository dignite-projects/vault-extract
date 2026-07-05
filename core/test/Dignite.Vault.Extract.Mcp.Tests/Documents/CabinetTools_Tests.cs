using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class CabinetToolsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ICabinetReadAppService>());
    }
}

public class CabinetTools_Tests : VaultExtractTestBase<CabinetToolsTestModule>
{
    private readonly ICabinetReadAppService _cabinetReadAppService;

    public CabinetTools_Tests()
    {
        _cabinetReadAppService = GetRequiredService<ICabinetReadAppService>();
    }

    [Fact]
    public async Task Returns_sorted_wrapped_cabinets_with_resource_uris()
    {
        var legalId = Guid.NewGuid();
        var financeId = Guid.NewGuid();
        _cabinetReadAppService.GetListAsync().Returns(
            new PagedResultDto<CabinetDto>(2, new List<CabinetDto>
            {
                new() { Id = financeId, Name = "Finance", Description = "Invoices" },
                new() { Id = legalId, Name = "Legal", Description = "Contracts" }
            }));

        var result = await CabinetTools.ListAsync(_cabinetReadAppService);

        result.TotalCount.ShouldBe(2);
        result.Truncated.ShouldBeFalse();
        result.Items[0].Id.ShouldBe(financeId);
        result.Items[0].Uri.ShouldBe(CabinetResourceUri.Format(financeId));
        result.Items[0].Name.ShouldBe(PromptBoundary.WrapField("Finance"));
        result.Items[0].Description.ShouldBe(PromptBoundary.WrapField("Invoices"));
        result.Items[1].Id.ShouldBe(legalId);
    }

    [Fact]
    public async Task Truncates_beyond_cap_and_signals_total()
    {
        var total = CabinetReadConsts.MaxResultCount + 5;
        _cabinetReadAppService.GetListAsync().Returns(
            new PagedResultDto<CabinetDto>(
                total,
                Enumerable.Range(0, CabinetReadConsts.MaxResultCount)
                .Select(i => new CabinetDto
                {
                    Id = Guid.NewGuid(),
                    Name = $"Cabinet {i:D4}"
                })
                .ToList()));

        var result = await CabinetTools.ListAsync(_cabinetReadAppService);

        result.Items.Count.ShouldBe(CabinetReadConsts.MaxResultCount);
        result.TotalCount.ShouldBe(total);
        result.Truncated.ShouldBeTrue();
        result.Items[0].Name.ShouldBe(PromptBoundary.WrapField("Cabinet 0000"));
    }
}
