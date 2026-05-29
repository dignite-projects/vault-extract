using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// <see cref="DocumentAppService.GetListAsync"/> 字段值过滤的输入校验 + 字段定义解析行为（验证上移重构）。
/// 复用 <see cref="DocumentAppServiceReviewTestModule"/> 的 mock 仓储；这些用例都在触达 <c>GetQueryableAsync</c>
/// 之前短路——DTO 校验抛 <see cref="AbpValidationException"/>（loud，替掉旧的静默空）/ 未定义字段抛
/// <see cref="BusinessException"/>，故无需真实 DB。实际字段值匹配（<c>GetFieldMatchedIdsAsync</c> 的
/// Documents-anchored LINQ）由 <c>EfCoreDocumentRepositorySearch_Tests</c> 在真实 EF（SQLite）上覆盖。
/// </summary>
public class DocumentAppService_FieldFilter_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;

    public DocumentAppService_FieldFilter_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
    }

    [Fact]
    public async Task Requires_document_type_when_field_filters_present()
    {
        // 无 DocumentTypeCode + 有 FieldFilters → GetDocumentListInput.Validate 失败（字段值离开类型无确定含义）。
        await Should.ThrowAsync<AbpValidationException>(() => _appService.GetListAsync(new GetDocumentListInput
        {
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } }
        }));
    }

    [Fact]
    public async Task Rejects_filter_without_value()
    {
        // filter 无任何值（等值 / 区间皆缺）→ DocumentFieldFilter.Validate 失败。
        await Should.ThrowAsync<AbpValidationException>(() => _appService.GetListAsync(new GetDocumentListInput
        {
            DocumentTypeCode = "host.contract",
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "amount" } }
        }));
    }

    [Fact]
    public async Task Rejects_invalid_field_name()
    {
        // 字段名含白名单外字符 → [RegularExpression] 失败。
        await Should.ThrowAsync<AbpValidationException>(() => _appService.GetListAsync(new GetDocumentListInput
        {
            DocumentTypeCode = "host.contract",
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "bad name!", Value = "x" } }
        }));
    }

    [Fact]
    public async Task Rejects_too_many_field_filters()
    {
        var tooMany = Enumerable.Range(0, DocumentConsts.MaxSearchFieldFilters + 1)
            .Select(i => new DocumentFieldFilter { Name = $"f{i}", Value = "x" })
            .ToList();

        // 超过 MaxSearchFieldFilters → [MaxLength] 失败。
        await Should.ThrowAsync<AbpValidationException>(() => _appService.GetListAsync(new GetDocumentListInput
        {
            DocumentTypeCode = "host.contract",
            FieldFilters = tooMany
        }));
    }

    [Fact]
    public async Task Throws_when_field_not_defined_on_type()
    {
        // #207：AppService 先把 TypeCode 解析为内部 DocumentTypeId，再按 Id 解析字段。
        var typeId = Guid.NewGuid();
        _documentTypeRepository
            .FindByTypeCodeAsync("host.contract", Arg.Any<CancellationToken>())
            .Returns(new DocumentType(typeId, null, "host.contract", "Contract"));
        _fieldDefinitionRepository
            .FindByNameAsync(typeId, "ghost", Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);

        // DTO 校验通过，但该类型下无 "ghost" 字段 → loud fail（UnknownExtractedField，可纠正），不静默空。
        var ex = await Should.ThrowAsync<BusinessException>(() => _appService.GetListAsync(new GetDocumentListInput
        {
            DocumentTypeCode = "host.contract",
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "ghost", Value = "x" } }
        }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.ExtractedField.Unknown);
    }
}
