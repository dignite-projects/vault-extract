using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Mcp.Documents;

[DependsOn(typeof(PaperbaseTestBaseModule))]
public class DocumentTypeResourcesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // ReadAsync 直接 CheckAsync(IAuthorizationService)——用 always-allow 让授权通过，聚焦 schema 投影行为。
        context.Services.AddAlwaysAllowAuthorization();
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
    }
}

/// <summary>
/// <see cref="DocumentTypeResources"/> read 行为：按 type code 返回字段 schema——DisplayName（类型 + 字段）
/// 经 <c>PromptBoundary</c> 包裹、字段按 DisplayOrder 排序、找不到类型抛 <see cref="McpException"/>。
/// 权限断言 / 租户隔离在框架层（此处 always-allow + mock 仓储），由其它测试覆盖、不在此重复。
/// resources/list 动态枚举是 MCP server 集成行为，不在单元测试范畴。
/// </summary>
public class DocumentTypeResources_Tests : PaperbaseTestBase<DocumentTypeResourcesTestModule>
{
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IAuthorizationService _authorizationService;

    public DocumentTypeResources_Tests()
    {
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _authorizationService = GetRequiredService<IAuthorizationService>();
    }

    [Fact]
    public async Task Returns_schema_with_wrapped_display_names_ordered_by_display_order()
    {
        // #207：ReadAsync 先 FindByTypeCodeAsync 拿类型 Id，再 GetByDocumentTypeAsync(id) 取字段。
        var typeId = Guid.NewGuid();
        _documentTypeRepository
            .FindByTypeCodeAsync("contract.general", Arg.Any<CancellationToken>())
            .Returns(new DocumentType(typeId, null, "contract.general", "合同"));
        _fieldDefinitionRepository
            .GetByDocumentTypeAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new(Guid.NewGuid(), null, typeId, "amount", "合同金额",
                    "Extract the total contract amount", FieldDataType.Number, displayOrder: 1, isRequired: true),
                new(Guid.NewGuid(), null, typeId, "partyName", "甲方",
                    "Extract party A name", FieldDataType.String, displayOrder: 0)
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeRepository, _fieldDefinitionRepository, _authorizationService);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.TypeCode.ShouldBe("contract.general");
        // 类型 / 字段 DisplayName 是 admin 配置文本，经 PromptBoundary 包裹防 indirect prompt injection。
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("合同"));
        // 字段按 DisplayOrder 升序：partyName(0) 先于 amount(1)。
        schema.Fields.Count.ShouldBe(2);
        schema.Fields[0].Name.ShouldBe("partyName");
        schema.Fields[0].DataType.ShouldBe("String");
        schema.Fields[0].DisplayName.ShouldBe(PromptBoundary.WrapField("甲方"));
        schema.Fields[1].Name.ShouldBe("amount");
        schema.Fields[1].DataType.ShouldBe("Number");
        schema.Fields[1].IsRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Throws_when_type_not_found()
    {
        // 跨租户 / 不存在的 code → FindByTypeCodeAsync 返回 null（租户隔离由 ambient 过滤器施加）。
        _documentTypeRepository
            .FindByTypeCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((DocumentType?)null);

        await Should.ThrowAsync<McpException>(async () =>
            await DocumentTypeResources.ReadAsync(
                "nonexistent", _documentTypeRepository, _fieldDefinitionRepository, _authorizationService));
    }
}
