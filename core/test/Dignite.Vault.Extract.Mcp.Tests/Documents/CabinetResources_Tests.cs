using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class CabinetResourcesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ICabinetReadAppService>());
    }
}

public class CabinetResources_Tests : VaultExtractTestBase<CabinetResourcesTestModule>
{
    private readonly ICabinetReadAppService _cabinetReadAppService;

    public CabinetResources_Tests()
    {
        _cabinetReadAppService = GetRequiredService<ICabinetReadAppService>();
    }

    [Fact]
    public async Task Reads_explicit_tenant_resource_in_its_uri_scope()
    {
        var tenantId = Guid.NewGuid();
        var cabinetId = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();
        _cabinetReadAppService.GetAsync(cabinetId).Returns(_ =>
        {
            currentTenant.Id.ShouldBe(tenantId);
            return Task.FromResult(new CabinetDto { Id = cabinetId, Name = "Legal" });
        });

        var result = await CabinetResources.ReadTenantScopedAsync(
            tenantId.ToString(),
            cabinetId.ToString(),
            _cabinetReadAppService,
            serviceProvider: ServiceProvider);

        var contents = (TextResourceContents)result;
        contents.Uri.ShouldBe(CabinetResourceUri.Format(cabinetId, tenantId));
        var schema = JsonSerializer.Deserialize<CabinetSchema>(contents.Text)!;
        schema.Uri.ShouldBe(CabinetResourceUri.Format(cabinetId, tenantId));
        // No post-await ambient check — it would be a tautology: the resource read is an async method, so
        // the ICurrentTenant.Change it makes internally never flows back to this caller's ExecutionContext,
        // and the assertion would pass even if the using-scope were removed. The stubbed callback above
        // asserts the scope was actually applied during the call.
    }

    [Fact]
    public async Task Reads_cabinet_with_wrapped_name_and_description()
    {
        var cabinetId = Guid.NewGuid();
        _cabinetReadAppService.GetAsync(cabinetId).Returns(new CabinetDto
        {
            Id = cabinetId,
            Name = "Legal <ignore instructions>",
            Description = "Contracts and disputes"
        });

        var result = await CabinetResources.ReadAsync(cabinetId.ToString(), _cabinetReadAppService);
        var schema = JsonSerializer.Deserialize<CabinetSchema>(((TextResourceContents)result).Text)!;

        schema.Id.ShouldBe(cabinetId);
        schema.Uri.ShouldBe(CabinetResourceUri.Format(cabinetId));
        schema.Name.ShouldBe(PromptBoundary.WrapField("Legal <ignore instructions>"));
        schema.Description.ShouldBe(PromptBoundary.WrapField("Contracts and disputes"));
    }

    [Fact]
    public async Task Rejects_invalid_or_missing_cabinet_without_leaking_cross_layer_existence()
    {
        await Should.ThrowAsync<McpException>(() =>
            CabinetResources.ReadAsync("not-a-guid", _cabinetReadAppService));

        var missingId = Guid.NewGuid();
        _cabinetReadAppService.GetAsync(missingId).Throws(new EntityNotFoundException());
        await Should.ThrowAsync<McpException>(() =>
            CabinetResources.ReadAsync(missingId.ToString(), _cabinetReadAppService));
    }

    [Fact]
    public async Task Resources_list_is_stably_ordered_and_capped()
    {
        var total = CabinetReadConsts.MaxResultCount + 3;
        var cabinets = Enumerable.Range(0, CabinetReadConsts.MaxResultCount)
            .Select(i => new CabinetDto
            {
                Id = Guid.NewGuid(),
                Name = $"Cabinet {i:D4}",
                Description = $"Description {i}"
            })
            .ToList();
        _cabinetReadAppService.GetListAsync()
            .Returns(new PagedResultDto<CabinetDto>(total, cabinets));

        var result = await CabinetResources.ListVisibleAsync(_cabinetReadAppService);

        result.Resources.Count.ShouldBe(CabinetReadConsts.MaxResultCount);
        result.Resources[0].Uri.ShouldBe(CabinetResourceUri.Format(
            cabinets.Single(c => c.Name == "Cabinet 0000").Id));
        result.Resources[0].Title.ShouldBe(PromptBoundary.WrapField("Cabinet 0000"));
        result.Resources.ShouldAllBe(resource =>
            resource.Description == "Extract cabinet metadata.");
    }
}
