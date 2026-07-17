using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.Fields;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class FieldSchemaPromptBudgetTestModule : AbpModule
{
    public const int PromptBudget = 10;

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<VaultExtractBehaviorOptions>(options =>
        {
            options.MaxFieldSchemaPromptLength = PromptBudget;
        });
    }
}

/// <summary>
/// #468 aggregate-budget coverage for field CRUD and restore paths against real repositories.
/// </summary>
public class FieldSchemaPromptBudget_Tests : VaultExtractTestBase<FieldSchemaPromptBudgetTestModule>
{
    private readonly IFieldDefinitionAppService _fieldAppService;
    private readonly IDocumentTypeAppService _typeAppService;
    private readonly VaultExtractBehaviorOptions _options;

    public FieldSchemaPromptBudget_Tests()
    {
        _fieldAppService = GetRequiredService<IFieldDefinitionAppService>();
        _typeAppService = GetRequiredService<IDocumentTypeAppService>();
        _options = GetRequiredService<IOptions<VaultExtractBehaviorOptions>>().Value;
    }

    [Fact]
    public async Task Create_rejects_a_projected_schema_above_the_type_budget()
    {
        var type = await CreateTypeAsync();
        await CreateFieldAsync(type.Id, "first", new string('a', 6));

        var ex = await Should.ThrowAsync<BusinessException>(
            () => CreateFieldAsync(type.Id, "second", new string('b', 5)));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.SchemaPromptBudgetExceeded);
        ex.Data["ActualLength"].ShouldBe(11L);
        ex.Data["MaxLength"].ShouldBe(FieldSchemaPromptBudgetTestModule.PromptBudget);
    }

    [Fact]
    public async Task Update_accepts_the_exact_budget_and_rejects_one_character_more()
    {
        var type = await CreateTypeAsync();
        await CreateFieldAsync(type.Id, "first", new string('a', 6));
        var second = await CreateFieldAsync(type.Id, "second", new string('b', 3));

        var atLimit = await _fieldAppService.UpdateAsync(second.Id, Update(second, new string('b', 4)));
        atLimit.Prompt!.Length.ShouldBe(FieldSchemaPromptBudgetTestModule.PromptBudget - 6);

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _fieldAppService.UpdateAsync(second.Id, Update(second, new string('b', 5))));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.SchemaPromptBudgetExceeded);
    }

    [Fact]
    public async Task Restoring_a_field_validates_the_projected_active_schema()
    {
        var type = await CreateTypeAsync();
        await CreateFieldAsync(type.Id, "first", new string('a', 6));
        var deleted = await CreateFieldAsync(type.Id, "deleted", new string('b', 4));
        await _fieldAppService.DeleteAsync(deleted.Id);
        await CreateFieldAsync(type.Id, "replacement", new string('c', 4));

        var ex = await Should.ThrowAsync<BusinessException>(() => _fieldAppService.RestoreAsync(deleted.Id));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.SchemaPromptBudgetExceeded);
        ex.Data["ActualLength"].ShouldBe(14L);
    }

    [Fact]
    public async Task Cascading_type_restore_rechecks_the_current_host_budget()
    {
        var type = await CreateTypeAsync();
        await CreateFieldAsync(type.Id, "only", new string('a', FieldSchemaPromptBudgetTestModule.PromptBudget));
        await _typeAppService.DeleteAsync(type.Id);

        _options.MaxFieldSchemaPromptLength = FieldSchemaPromptBudgetTestModule.PromptBudget - 1;
        try
        {
            var ex = await Should.ThrowAsync<BusinessException>(() => _typeAppService.RestoreAsync(type.Id));
            ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.SchemaPromptBudgetExceeded);
        }
        finally
        {
            _options.MaxFieldSchemaPromptLength = FieldSchemaPromptBudgetTestModule.PromptBudget;
        }
    }

    private async Task<DocumentTypeDto> CreateTypeAsync()
        => await _typeAppService.CreateAsync(new CreateDocumentTypeDto
        {
            TypeCode = $"host.budget-{Guid.NewGuid():N}",
            DisplayName = "Budget test"
        });

    private async Task<FieldDefinitionDto> CreateFieldAsync(Guid documentTypeId, string name, string? prompt)
        => await _fieldAppService.CreateAsync(new CreateFieldDefinitionDto
        {
            DocumentTypeId = documentTypeId,
            Name = name,
            DisplayName = name,
            Prompt = prompt,
            DataType = FieldDataType.Text
        });

    private static UpdateFieldDefinitionDto Update(FieldDefinitionDto field, string? prompt)
        => new()
        {
            Name = field.Name,
            DisplayName = field.DisplayName,
            Prompt = prompt,
            DataType = field.DataType,
            DisplayOrder = field.DisplayOrder,
            IsRequired = field.IsRequired,
            AllowMultiple = field.AllowMultiple,
            IsUniqueKey = field.IsUniqueKey
        };
}
