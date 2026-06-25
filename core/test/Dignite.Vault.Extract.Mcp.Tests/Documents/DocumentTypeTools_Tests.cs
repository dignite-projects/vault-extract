using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(ExtractTestBaseModule))]
public class DocumentTypeToolsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionAppService>());
    }
}

/// <summary>
/// Thin-shell behavior of <see cref="DocumentTypeTools.ListAsync"/>: delegates to
/// <see cref="IDocumentTypeAppService.GetVisibleAsync"/> plus
/// <see cref="IFieldDefinitionAppService.GetListAsync"/> in one batch with DocumentTypeId left blank to
/// eliminate per-type N+1, and maps results to <see cref="DocumentTypeListResult"/>. displayName is
/// wrapped by <c>PromptBoundary</c>; results are sorted by TypeCode and truncated to
/// <see cref="ExtractMcpConsts.MaxDocumentTypeResults"/>, with truncated/totalCount signals when over
/// limit.
/// </summary>
public class DocumentTypeTools_Tests : ExtractTestBase<DocumentTypeToolsTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;

    public DocumentTypeTools_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
    }

    [Fact]
    public async Task Returns_types_with_fields_and_wraps_display_names()
    {
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>
            {
                new()
                {
                    Id = typeId,
                    TypeCode = "contract.general",
                    DisplayName = "General Contract"
                }
            });
        // Batch path: leave DocumentTypeId blank to fetch all current-layer field definitions once, then
        // group in tool memory by DocumentTypeId, eliminating N+1.
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == null && !i.OnlyDeleted))
            .Returns(new List<FieldDefinitionDto>
            {
                new()
                {
                    DocumentTypeId = typeId,
                    Name = "amount",
                    DataType = FieldDataType.Number,
                    AllowMultiple = false,
                    DisplayName = "Amount",
                    IsRequired = true,
                    DisplayOrder = 0
                },
                new()
                {
                    DocumentTypeId = typeId,
                    Name = "party_name",
                    DataType = FieldDataType.Text,
                    AllowMultiple = false,
                    DisplayName = "Party Name",
                    IsRequired = false,
                    DisplayOrder = 1
                }
            });

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.TotalCount.ShouldBe(1);
        result.Truncated.ShouldBeFalse();
        result.Types.Count.ShouldBe(1);
        var schema = result.Types[0];
        schema.TypeCode.ShouldBe("contract.general");
        // DisplayName must be wrapped by PromptBoundary.
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("General Contract"));
        schema.Fields.Count.ShouldBe(2);

        var amountField = schema.Fields[0];
        amountField.Name.ShouldBe("amount");
        amountField.DataType.ShouldBe("Number");
        amountField.AllowMultiple.ShouldBeFalse();
        amountField.IsRequired.ShouldBeTrue();
        amountField.DisplayName.ShouldBe(PromptBoundary.WrapField("Amount"));

        var partyField = schema.Fields[1];
        partyField.Name.ShouldBe("party_name");
        partyField.DataType.ShouldBe("Text");
        partyField.IsRequired.ShouldBeFalse();

        // N+1 guard: field definitions are allowed only one batch call, with no per-type query loop.
        await _fieldDefinitionAppService.Received(1).GetListAsync(Arg.Any<GetFieldDefinitionListInput>());
    }

    [Fact]
    public async Task Returns_empty_list_when_no_visible_types()
    {
        _documentTypeAppService.GetVisibleAsync().Returns(new List<DocumentTypeDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.Types.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        result.Truncated.ShouldBeFalse();
        await _fieldDefinitionAppService.DidNotReceive().GetListAsync(Arg.Any<GetFieldDefinitionListInput>());
    }

    [Fact]
    public async Task Within_cap_returns_all_types_without_truncation_signal()
    {
        // Exactly at the limit: return all results with no truncation signal; within-limit behavior is
        // unchanged.
        var total = ExtractMcpConsts.MaxDocumentTypeResults;
        _documentTypeAppService.GetVisibleAsync().Returns(BuildTypes(total));
        _fieldDefinitionAppService
            .GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(new List<FieldDefinitionDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.Types.Count.ShouldBe(total);
        result.TotalCount.ShouldBe(total);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Truncates_types_beyond_cap_and_signals_truncation()
    {
        // Hard result-set limit (llm-call-anti-patterns counterexample B point 3): tenant admins can
        // create arbitrarily many types. Over-limit results must be truncated and explicitly tell the LLM
        // there are more via truncated + totalCount.
        var total = ExtractMcpConsts.MaxDocumentTypeResults + 5;
        _documentTypeAppService.GetVisibleAsync().Returns(BuildTypes(total));
        _fieldDefinitionAppService
            .GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(new List<FieldDefinitionDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.Types.Count.ShouldBe(ExtractMcpConsts.MaxDocumentTypeResults);
        result.TotalCount.ShouldBe(total);
        result.Truncated.ShouldBeTrue();
        // Stable sort by TypeCode before truncation, independent from AppService return order. Keep the
        // lexicographically first segment and discard the tail.
        result.Types[0].TypeCode.ShouldBe(TypeCodeOf(0));
        result.Types[^1].TypeCode.ShouldBe(TypeCodeOf(ExtractMcpConsts.MaxDocumentTypeResults - 1));
        // Truncation must not amplify query count: field definitions still allow only one batch call.
        await _fieldDefinitionAppService.Received(1).GetListAsync(
            Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == null));
    }

    private static string TypeCodeOf(int index) => $"type.{index:D4}";

    private static List<DocumentTypeDto> BuildTypes(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new DocumentTypeDto
            {
                Id = Guid.NewGuid(),
                TypeCode = TypeCodeOf(i),
                DisplayName = $"Type {i}"
            })
            // Feed unordered input into the tool; truncation must be based on the tool's own stable
            // TypeCode sorting.
            .OrderByDescending(t => t.TypeCode, StringComparer.Ordinal)
            .ToList();
    }
}
