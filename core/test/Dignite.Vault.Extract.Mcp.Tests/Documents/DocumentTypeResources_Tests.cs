using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
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

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class DocumentTypeResourcesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Output is a thin shell delegating to AppService. Permission assertions / tenant isolation live
        // in AppService and are represented here by a mock substitute. The injected mock asserts code
        // filtering, schema projection, PromptBoundary wrapping, and not-found behavior.
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionAppService>());
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

    public DocumentTypeResources_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
    }

    [Fact]
    public async Task Reads_explicit_tenant_resource_in_its_uri_scope()
    {
        var tenantId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();
        var ambientTenantId = currentTenant.Id;
        _documentTypeAppService.GetVisibleAsync().Returns(_ =>
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
            serviceProvider: ServiceProvider);

        var contents = (TextResourceContents)result;
        contents.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general", tenantId));
        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(contents.Text)!;
        schema.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general", tenantId));
        currentTenant.Id.ShouldBe(ambientTenantId);
    }

    [Fact]
    public async Task Returns_schema_with_wrapped_display_names_ordered_by_display_order()
    {
        // #222: ReadAsync delegates to GetVisibleAsync to filter type by code, then GetListAsync
        // (DocumentTypeId) to load fields (#207).
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync()
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
                    Prompt = "Extract the total contract amount", DataType = FieldDataType.Number,
                    DisplayOrder = 1, IsRequired = true
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Prompt = "Extract party A name", DataType = FieldDataType.Text, DisplayOrder = 0
                }
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeAppService, _fieldDefinitionAppService);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.TypeCode.ShouldBe("contract.general");
        schema.Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general"));
        // Type / field DisplayName values are admin-configured text and are wrapped by PromptBoundary to
        // defend against indirect prompt injection.
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("合同"));
        // Fields sort by DisplayOrder ascending: partyName(0) before amount(1).
        schema.Fields.Count.ShouldBe(2);
        schema.Fields[0].Name.ShouldBe("partyName");
        schema.Fields[0].DataType.ShouldBe("Text");
        schema.Fields[0].DisplayName.ShouldBe(PromptBoundary.WrapField("甲方"));
        schema.Fields[1].Name.ShouldBe("amount");
        schema.Fields[1].DataType.ShouldBe("Number");
        schema.Fields[1].IsRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Exposes_AllowMultiple_so_clients_know_a_field_returns_an_array()
    {
        // #212: multi-value fields are string[] in search result extractedFields. Schema must expose
        // AllowMultiple, otherwise MCP clients would parse arrays as "text scalar" and fail.
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync()
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
                    Prompt = "Extract tags", DataType = FieldDataType.Text, DisplayOrder = 0,
                    IsRequired = false, AllowMultiple = true
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Prompt = "Extract party A name", DataType = FieldDataType.Text, DisplayOrder = 1
                }
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeAppService, _fieldDefinitionAppService);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.Fields[0].Name.ShouldBe("tags");
        schema.Fields[0].AllowMultiple.ShouldBeTrue();
        schema.Fields[1].Name.ShouldBe("partyName");
        schema.Fields[1].AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Throws_when_type_not_found()
    {
        // Cross-tenant / nonexistent code is absent from the current-layer type set returned by
        // GetVisibleAsync; tenant isolation is enforced by ambient filters.
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>());

        await Should.ThrowAsync<McpException>(async () =>
            await DocumentTypeResources.ReadAsync(
                "nonexistent", _documentTypeAppService, _fieldDefinitionAppService));
    }

    [Fact]
    public async Task Resources_list_projects_visible_types_ordered_by_type_code()
    {
        // Within-limit behavior: one Resource per visible type, URI / Name by TypeCode, sorted stably by
        // TypeCode.
        _documentTypeAppService
            .GetVisibleAsync()
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
        _documentTypeAppService.GetVisibleAsync().Returns(types);

        var result = await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);

        result.Resources.Count.ShouldBe(VaultExtractMcpConsts.MaxDocumentTypeResults);
        // Keep the lexicographically first TypeCode segment and discard the tail.
        result.Resources[0].Name.ShouldBe("type.0000");
        result.Resources[^1].Name.ShouldBe($"type.{VaultExtractMcpConsts.MaxDocumentTypeResults - 1:D4}");
    }
}
