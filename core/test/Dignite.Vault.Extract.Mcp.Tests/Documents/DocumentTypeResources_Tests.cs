using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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
public class DocumentTypeResourcesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Output is a thin shell delegating to AppService. Permission assertions / tenant isolation live
        // in AppService and are represented here by a mock substitute. The injected mock asserts code
        // filtering, schema projection, PromptBoundary wrapping, and not-found behavior.
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
/// Read behavior of <see cref="DocumentTypeResources"/>: returns field schema by type code. DisplayName
/// values for type and fields are wrapped by <c>PromptBoundary</c>, fields are sorted by DisplayOrder,
/// and missing types throw <see cref="McpException"/>.
/// Permission assertions, parameter validation, and tenant isolation live in AppService, represented here
/// by a mock substitute, so those behaviors are covered by AppService tests and not repeated here.
/// resources/list projection logic (<see cref="DocumentTypeResources.ListVisibleAsync"/>, delegated by
/// the module handler) is also covered here: stable TypeCode sorting plus hard-limit truncation
/// (<see cref="VaultExtractMcpConsts.MaxDocumentTypeResults"/>).
/// </summary>
public class DocumentTypeResources_Tests : VaultExtractTestBase<DocumentTypeResourcesTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly IFieldTypeResolver _fieldTypeResolver;
    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;

    public DocumentTypeResources_Tests()
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
    public async Task Reads_explicit_tenant_resource_in_its_uri_scope()
    {
        var tenantId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();
        _documentTypeAppService.GetVisibleAsync(Arg.Any<bool>()).Returns(_ =>
        {
            currentTenant.Id.ShouldBe(tenantId);
            return Task.FromResult<List<DocumentTypeDto>>(
            [new() { Id = typeId, TypeCode = "contract.general", DisplayName = "General Contract" }]);
        });
        _fieldDefinitionAppService.GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(Task.FromResult<List<FieldDefinitionDto>>([]));

        var result = await DocumentTypeResources.ReadTenantScopedAsync(
            tenantId.ToString(),
            "contract.general",
            _documentTypeAppService,
            _fieldDefinitionAppService,
            _fieldTypeResolver,
            _fieldTypeExtensionRegistry,
            serviceProvider: ServiceProvider);

        var contents = (TextResourceContents)result;
        contents.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general", tenantId));
        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(contents.Text)!;
        schema.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general", tenantId));
        // No post-await ambient check — it would be a tautology: the resource read is an async method, so
        // the ICurrentTenant.Change it makes internally never flows back to this caller's ExecutionContext,
        // and the assertion would pass even if the using-scope were removed. The stubbed callback above
        // asserts the scope was actually applied during the call.
    }

    /// <summary>
    /// #524 acceptance criterion: a blank/whitespace mandatory <c>{tenantId}</c> uri segment must error,
    /// not silently read the ambient tenant under a uri that names a different one.
    /// </summary>
    [Fact]
    public async Task Rejects_blank_mandatory_tenant_segment_instead_of_falling_back_to_ambient()
    {
        await Should.ThrowAsync<McpException>(() =>
            DocumentTypeResources.ReadTenantScopedAsync(
                " ",
                "contract.general",
                _documentTypeAppService,
                _fieldDefinitionAppService,
                _fieldTypeResolver,
                _fieldTypeExtensionRegistry,
                serviceProvider: ServiceProvider));

        await _documentTypeAppService.DidNotReceive().GetVisibleAsync(Arg.Any<bool>());
    }

    [Fact]
    public async Task Returns_schema_with_wrapped_display_names_ordered_by_display_order()
    {
        // #222: ReadAsync delegates to GetVisibleAsync to filter type by code, then GetListAsync
        // (DocumentTypeId) to load fields (#207).
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync(Arg.Any<bool>())
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = typeId, TypeCode = "contract.general", DisplayName = "合同" }
            });
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == typeId))
            .Returns(new List<FieldDefinitionDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "amount", DisplayName = "合同金额",
                    Description = "Extract the total contract amount", FieldTypeName = NumberFieldType.ControlName,
                    DisplayOrder = 1, IsRequired = true
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Description = "Extract party A name", FieldTypeName = TextFieldType.ControlName, DisplayOrder = 0
                }
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver,
            _fieldTypeExtensionRegistry);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.TypeCode.ShouldBe("contract.general");
        schema.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general"));
        // Type / field DisplayName values are admin-configured text and are wrapped by PromptBoundary to
        // defend against indirect prompt injection.
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("合同"));
        // Fields sort by DisplayOrder ascending: partyName(0) before amount(1).
        schema.Fields.Count.ShouldBe(2);
        schema.Fields[0].Name.ShouldBe("partyName");
        schema.Fields[0].FieldType.ShouldBe(TextFieldType.ControlName);
        schema.Fields[0].DisplayName.ShouldBe(PromptBoundary.WrapField("甲方"));
        schema.Fields[1].Name.ShouldBe("amount");
        schema.Fields[1].FieldType.ShouldBe(NumberFieldType.ControlName);
        schema.Fields[1].IsRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Exposes_IsMultiValue_so_clients_know_a_field_returns_an_array()
    {
        // #212: multi-value fields are string[] in search result extractedFields. The schema must say so,
        // or MCP clients parse an array as a text scalar and fail. In v3 the fact lives in the field type
        // (Tags) rather than in a flag beside it, and the schema keeps exposing it as its own boolean so a
        // client never has to know which registration keys happen to be multi-valued.
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync(Arg.Any<bool>())
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = typeId, TypeCode = "contract.general", DisplayName = "合同" }
            });
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == typeId))
            .Returns(new List<FieldDefinitionDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "tags", DisplayName = "标签",
                    Description = "Extract tags", FieldTypeName = TagsFieldType.ControlName, DisplayOrder = 0,
                    IsRequired = false
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Description = "Extract party A name", FieldTypeName = TextFieldType.ControlName, DisplayOrder = 1
                }
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver,
            _fieldTypeExtensionRegistry);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.Fields[0].Name.ShouldBe("tags");
        schema.Fields[0].IsMultiValue.ShouldBeTrue();
        schema.Fields[1].Name.ShouldBe("partyName");
        schema.Fields[1].IsMultiValue.ShouldBeFalse();
    }

    [Fact]
    public async Task Throws_when_type_not_found()
    {
        // Cross-tenant / nonexistent code is absent from the current-layer type set returned by
        // GetVisibleAsync; tenant isolation is enforced by ambient filters.
        _documentTypeAppService
            .GetVisibleAsync(Arg.Any<bool>())
            .Returns(new List<DocumentTypeDto>());

        await Should.ThrowAsync<McpException>(async () =>
            await DocumentTypeResources.ReadAsync(
                "nonexistent", _documentTypeAppService, _fieldDefinitionAppService, _fieldTypeResolver,
                _fieldTypeExtensionRegistry));
    }

    [Fact]
    public async Task Resources_list_projects_visible_types_ordered_by_type_code()
    {
        // Within-limit behavior: one Resource per visible type, URI / Name by TypeCode, sorted stably by
        // TypeCode.
        _documentTypeAppService
            .GetVisibleAsync(Arg.Any<bool>())
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = Guid.NewGuid(), TypeCode = "invoice.vat", DisplayName = "增值税发票" },
                new() { Id = Guid.NewGuid(), TypeCode = "contract.general", DisplayName = "合同" }
            });

        var result = await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);

        result.Resources.Count.ShouldBe(2);
        result.Resources[0].Name.ShouldBe("contract.general");
        result.Resources[0].Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general"));
        result.Resources[0].MimeType.ShouldBe("application/json");
        result.Resources[1].Name.ShouldBe("invoice.vat");
        result.Resources[1].Uri.ShouldBe(DocumentTypeResourceUri.Format("invoice.vat"));
    }

    [Fact]
    public async Task Resources_list_truncates_beyond_cap()
    {
        // Hard result-set limit (llm-call-anti-patterns counterexample B point 3): tenant admins can
        // create arbitrarily many types. resources/list protocol entries have no place to carry a
        // truncation signal, so truncate directly; full discovery goes through the list_document_types
        // tool.
        var total = VaultExtractMcpConsts.MaxDocumentTypeResults + 3;
        var types = Enumerable.Range(0, total)
            .Select(i => new DocumentTypeDto
            {
                Id = Guid.NewGuid(),
                TypeCode = $"type.{i:D4}",
                DisplayName = $"Type {i}"
            })
            // Feed unordered input into projection; truncation must be based on the projection's own
            // stable TypeCode sorting.
            .OrderByDescending(t => t.TypeCode, StringComparer.Ordinal)
            .ToList();
        _documentTypeAppService.GetVisibleAsync(Arg.Any<bool>()).Returns(types);

        var result = await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);

        result.Resources.Count.ShouldBe(VaultExtractMcpConsts.MaxDocumentTypeResults);
        // Keep the lexicographically first TypeCode segment and discard the tail.
        result.Resources[0].Name.ShouldBe("type.0000");
        result.Resources[^1].Name.ShouldBe($"type.{VaultExtractMcpConsts.MaxDocumentTypeResults - 1:D4}");
    }

    /// <summary>
    /// #632 (#629 leftover): both resource entry points — the resources/list projection and the per-type schema
    /// read — must ask <c>GetVisibleAsync</c> to skip ABP's <c>ResourcePermissionPopulator</c>. Neither reads
    /// <c>DocumentTypeDto.ResourcePermissions</c>; filling it is one multi-permission check per type for nothing.
    /// </summary>
    [Fact]
    public async Task Both_resource_entry_points_skip_the_resource_permission_populator()
    {
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync(Arg.Any<bool>())
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = typeId, TypeCode = "contract.general", DisplayName = "合同" }
            });
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == typeId))
            .Returns(new List<FieldDefinitionDto>());

        await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);
        await DocumentTypeResources.ReadAsync(
            "contract.general",
            _documentTypeAppService,
            _fieldDefinitionAppService,
            _fieldTypeResolver,
            _fieldTypeExtensionRegistry);

        await _documentTypeAppService.Received(2).GetVisibleAsync(false);
        await _documentTypeAppService.DidNotReceive().GetVisibleAsync(true);
    }
}
