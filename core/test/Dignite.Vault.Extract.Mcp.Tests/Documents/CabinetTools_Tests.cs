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
using Volo.Abp.MultiTenancy;
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
    public async Task Lists_in_explicit_tenant_and_returns_tenant_scoped_resource_uris()
    {
        var tenantId = Guid.NewGuid();
        var cabinetId = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();
        var ambientTenantId = currentTenant.Id;
        _cabinetReadAppService.GetListAsync().Returns(_ =>
        {
            currentTenant.Id.ShouldBe(tenantId);
            return Task.FromResult(new PagedResultDto<CabinetDto>(1,
            [new CabinetDto { Id = cabinetId, Name = "Legal" }]));
        });

        var result = await CabinetTools.ListAsync(
            _cabinetReadAppService,
            tenantId: tenantId.ToString(),
            serviceProvider: ServiceProvider);

        result.Items[0].Uri.ShouldBe(CabinetResourceUri.Format(cabinetId, tenantId));
        currentTenant.Id.ShouldBe(ambientTenantId);
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
