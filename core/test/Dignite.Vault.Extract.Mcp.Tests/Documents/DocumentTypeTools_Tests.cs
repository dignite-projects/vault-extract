using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.CKEditor;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Fields.FieldTypeExtensions;
using Dignite.Vault.Extract.FlexFields;
using Dignite.Vault.Extract.FlexFields.Tags;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

// VaultExtractFlexFieldsModule brings the kernel field-type registry the schema projection reads (isFilterable);
// stubbing it would let the projection agree with a registry that does not exist. IVaultExtractFieldTypeRegistry
// (isMultiValue) is Vault Extract's own, built-in extensions constructed directly here rather than via
// [DependsOn(VaultExtractApplicationModule)] - that module's own EF Core / background-job registration is not
// needed just to get the same 8 stateless extension classes it would auto-register.
[DependsOn(typeof(VaultExtractTestBaseModule), typeof(Extract.FlexFields.VaultExtractFlexFieldsModule))]
public class DocumentTypeToolsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionAppService>());
        var table = new TableFieldTypeExtension();
        var fieldTypeRegistry = new VaultExtractFieldTypeRegistry(
            new IVaultExtractFieldTypeExtension[]
            {
                new TextFieldTypeExtension(), new NumberFieldTypeExtension(), new BooleanFieldTypeExtension(),
                new DateTimeFieldTypeExtension(), new SelectFieldTypeExtension(), new CKEditorFieldTypeExtension(),
                new TagsFieldTypeExtension(), table
            });
        table.Registry = fieldTypeRegistry;
        context.Services.AddSingleton<IVaultExtractFieldTypeRegistry>(fieldTypeRegistry);

        // #524: explicit tenant scope is fail-closed by default. This suite's ad hoc Guid.NewGuid()
        // tenant ids need to clear that gate; the gate itself is covered separately (McpTenantAdmission_Tests /
        // McpPermissionResolution_Tests).
        context.Services.AllowAnyExplicitTenant();
    }
}

/// <summary>
/// Thin-shell behavior of <see cref="DocumentTypeTools.ListAsync"/>: delegates to
/// <see cref="IDocumentTypeAppService.GetVisibleSummariesAsync"/> plus
/// <see cref="IFieldDefinitionAppService.GetListAsync"/> in one batch with DocumentTypeId left blank to
/// eliminate per-type N+1, and maps results to <see cref="DocumentTypeListResult"/>. displayName is
/// wrapped by <c>PromptBoundary</c>; results are sorted by TypeCode and truncated to
/// <see cref="VaultExtractMcpConsts.MaxDocumentTypeResults"/>, with truncated/totalCount signals when over
/// limit.
/// </summary>
public class DocumentTypeTools_Tests : VaultExtractTestBase<DocumentTypeToolsTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly IFieldTypeResolver _fieldTypeResolver;
    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;

    public DocumentTypeTools_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        // The real resolver, not a substitute: the schema's isMultiValue / isFilterable come from the field
        // types actually registered, and a stubbed resolver would let a type this deployment does not have
        // pass for a filterable one.
        _fieldTypeResolver = GetRequiredService<IFieldTypeResolver>();
        _fieldTypeExtensionRegistry = GetRequiredService<IVaultExtractFieldTypeRegistry>();
    }

    [Fact]
    public async Task Lists_in_explicit_tenant_and_returns_tenant_scoped_resource_uris()
    {
        var tenantId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();
        _documentTypeAppService.GetVisibleSummariesAsync().Returns(_ =>
        {
            currentTenant.Id.ShouldBe(tenantId);
            return Task.FromResult<List<DocumentTypeSummaryDto>>(
            [
                new()
                {
                    Id = typeId,
                    TypeCode = "contract.general",
                    DisplayName = "General Contract"
                }
            ]);
        });
        _fieldDefinitionAppService.GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(Task.FromResult<List<FieldDefinitionDto>>([]));

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService,
            _fieldDefinitionAppService,
            _fieldTypeResolver,
            _fieldTypeExtensionRegistry,
            tenantId: tenantId.ToString(),
            serviceProvider: ServiceProvider);

        result.Types[0].Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general", tenantId));
        // No post-await ambient check — it would be a tautology: the tool is an async method, so the
        // ICurrentTenant.Change it makes internally never flows back to this caller's ExecutionContext,
        // and the assertion would pass even if the using-scope were removed. The stubbed callback above
        // asserts the scope was actually applied during the call.
    }

    /// <summary>
    /// #636 (closing the #629/#632 leftover "populator cost on MCP paths"): the MCP surface lists types for an
    /// LLM and never reads a caller's per-type grants, so it must call the narrow
    /// <c>GetVisibleSummariesAsync</c> read, which skips ABP's <c>ResourcePermissionPopulator</c> entirely — not
    /// the full <c>GetVisibleAsync</c>. Asserted once, here, rather than pinned into every setup in this file: the
    /// other tests only need the call to resolve.
    /// </summary>
    [Fact]
    public async Task List_tool_uses_the_narrow_summary_read()
    {
        _documentTypeAppService.GetVisibleSummariesAsync().Returns(new List<DocumentTypeSummaryDto>());

        await DocumentTypeTools.ListAsync(
            _documentTypeAppService,
            _fieldDefinitionAppService,
            _fieldTypeResolver,
            _fieldTypeExtensionRegistry);

        await _documentTypeAppService.Received(1).GetVisibleSummariesAsync();
        await _documentTypeAppService.DidNotReceive().GetVisibleAsync();
    }

    [Fact]
    public async Task Returns_types_with_fields_and_wraps_display_names()
    {
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleSummariesAsync()
            .Returns(new List<DocumentTypeSummaryDto>
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
                    FieldTypeName = NumberFieldType.ControlName,
                    DisplayName = "Amount",
                    IsRequired = true,
                    DisplayOrder = 0
                },
                new()
                {
                    DocumentTypeId = typeId,
                    Name = "party_name",
                    FieldTypeName = TextFieldType.ControlName,
                    DisplayName = "Party Name",
                    IsRequired = false,
                    DisplayOrder = 1
                }
            });

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver, _fieldTypeExtensionRegistry);

        result.TotalCount.ShouldBe(1);
        result.Truncated.ShouldBeFalse();
        result.Types.Count.ShouldBe(1);
        var schema = result.Types[0];
        schema.TypeCode.ShouldBe("contract.general");
        schema.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general"));
        // DisplayName must be wrapped by PromptBoundary.
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("General Contract"));
        schema.Fields.Count.ShouldBe(2);

        var amountField = schema.Fields[0];
        amountField.Name.ShouldBe("amount");
        amountField.FieldType.ShouldBe(NumberFieldType.ControlName);
        amountField.IsMultiValue.ShouldBeFalse();
        amountField.IsRequired.ShouldBeTrue();
        amountField.DisplayName.ShouldBe(PromptBoundary.WrapField("Amount"));

        var partyField = schema.Fields[1];
        partyField.Name.ShouldBe("party_name");
        partyField.FieldType.ShouldBe(TextFieldType.ControlName);
        partyField.IsRequired.ShouldBeFalse();

        // N+1 guard: field definitions are allowed only one batch call, with no per-type query loop.
        await _fieldDefinitionAppService.Received(1).GetListAsync(Arg.Any<GetFieldDefinitionListInput>());
    }

    [Fact]
    public async Task Returns_empty_list_when_no_visible_types()
    {
        _documentTypeAppService.GetVisibleSummariesAsync().Returns(new List<DocumentTypeSummaryDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver, _fieldTypeExtensionRegistry);

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
        var total = VaultExtractMcpConsts.MaxDocumentTypeResults;
        _documentTypeAppService.GetVisibleSummariesAsync().Returns(BuildTypes(total));
        _fieldDefinitionAppService
            .GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(new List<FieldDefinitionDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver, _fieldTypeExtensionRegistry);

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
        var total = VaultExtractMcpConsts.MaxDocumentTypeResults + 5;
        _documentTypeAppService.GetVisibleSummariesAsync().Returns(BuildTypes(total));
        _fieldDefinitionAppService
            .GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(new List<FieldDefinitionDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver, _fieldTypeExtensionRegistry);

        result.Types.Count.ShouldBe(VaultExtractMcpConsts.MaxDocumentTypeResults);
        result.TotalCount.ShouldBe(total);
        result.Truncated.ShouldBeTrue();
        // Stable sort by TypeCode before truncation, independent from AppService return order. Keep the
        // lexicographically first segment and discard the tail.
        result.Types[0].TypeCode.ShouldBe(TypeCodeOf(0));
        result.Types[^1].TypeCode.ShouldBe(TypeCodeOf(VaultExtractMcpConsts.MaxDocumentTypeResults - 1));
        // Truncation must not amplify query count: field definitions still allow only one batch call.
        await _fieldDefinitionAppService.Received(1).GetListAsync(
            Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == null));
    }

    private static string TypeCodeOf(int index) => $"type.{index:D4}";

    private static List<DocumentTypeSummaryDto> BuildTypes(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new DocumentTypeSummaryDto
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
