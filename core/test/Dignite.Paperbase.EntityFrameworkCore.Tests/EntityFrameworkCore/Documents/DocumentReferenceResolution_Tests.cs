using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BlobStoring;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

[DependsOn(typeof(PaperbaseEntityFrameworkCoreTestModule))]
public class DocumentReferenceResolutionTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 走真实 EF 仓储 + 真实 AppService（DTO 组装）；DocumentAppService 构造依赖 IBlobContainer，
        // 本测试不触发上传 / 下载，substitute 仅满足构造（避免未配置 blob provider 的解析失败）。
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
    }
}

/// <summary>
/// #207 验收：TypeCode / Name 重命名解锁 + soft-delete 穿透 join + DataType 变更守卫，端到端走真实 EF（SQLite）
/// + AppService（DTO 组装）。核心证明：内部用不可变 Id 关联，重命名不级联数据行而出口 DTO 透明反映当前 code/name；
/// 已归档（软删）字段仍可被历史文档读路径解析；已有抽取值的字段禁止改 DataType。
/// </summary>
public class DocumentReferenceResolution_Tests : PaperbaseTestBase<DocumentReferenceResolutionTestModule>
{
    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly IDocumentRepository _documentRepository;

    public DocumentReferenceResolution_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task Rename_TypeCode_And_FieldName_Is_Reflected_In_Dto_Without_Cascade()
    {
        Guid typeId = default;
        Guid fieldId = default;
        var docId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            (typeId, fieldId) = await SeedTypeFieldAndDocAsync(
                "contract.general", "amount", FieldDataType.Number, docId, 100m);
        });

        // 重命名前：DTO 输出原 code / name。
        await WithUnitOfWorkAsync(async () =>
        {
            var dto = await GetDtoAsync("contract.general", docId);
            dto.DocumentTypeCode.ShouldBe("contract.general");
            dto.ExtractedFields.ShouldNotBeNull();
            dto.ExtractedFields!.ShouldContainKey("amount");
        });

        // 重命名 TypeCode + Name（DataType 不变 → 不触发守卫）。内部 Id 不变，不级联 Document / 字段值行。
        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeAppService.UpdateAsync(typeId, new UpdateDocumentTypeDto
            {
                TypeCode = "contract.renamed",
                DisplayName = "Contract",
                ConfidenceThreshold = 0.7,
                Priority = 0
            });
            await _fieldDefinitionAppService.UpdateAsync(fieldId, new UpdateFieldDefinitionDto
            {
                Name = "total_amount",
                DisplayName = "Amount",
                Prompt = "Extract the amount.",
                DataType = FieldDataType.Number,
                DisplayOrder = 0,
                IsRequired = false
            });
        });

        // 重命名后：同一历史文档的 DTO 透明反映新 code / name（join 当前 DocumentType / FieldDefinition）。
        await WithUnitOfWorkAsync(async () =>
        {
            var dto = await GetDtoAsync("contract.renamed", docId);
            dto.DocumentTypeCode.ShouldBe("contract.renamed");
            dto.ExtractedFields.ShouldNotBeNull();
            dto.ExtractedFields!.ShouldContainKey("total_amount");
            dto.ExtractedFields.ShouldNotContainKey("amount");
        });
    }

    [Fact]
    public async Task Soft_Deleted_FieldDefinition_Name_Still_Resolves_For_Historical_Document()
    {
        Guid fieldId = default;
        var docId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            (_, fieldId) = await SeedTypeFieldAndDocAsync(
                "host.invoice", "partner", FieldDataType.String, docId, "Acme");
        });

        // 软删字段（FieldDefinition 删除走软删，无 in-use 守卫；字段值行仍在）。
        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.DeleteAsync(fieldId));

        // 历史文档读路径穿透 soft-delete join → 仍解析出（已归档）字段名，不丢 key。
        await WithUnitOfWorkAsync(async () =>
        {
            var dto = await GetDtoAsync("host.invoice", docId);
            dto.ExtractedFields.ShouldNotBeNull();
            dto.ExtractedFields!.ShouldContainKey("partner");
        });
    }

    [Fact]
    public async Task Changing_DataType_With_Existing_Field_Values_Is_Rejected()
    {
        Guid fieldId = default;
        var docId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            (_, fieldId) = await SeedTypeFieldAndDocAsync(
                "contract.general", "amount", FieldDataType.Number, docId, 100m);
        });

        // 已有抽取值的字段禁止改 DataType（否则历史值落在旧 typed 列、按新类型查会静默漏掉）。
        await WithUnitOfWorkAsync(async () =>
        {
            var ex = await Should.ThrowAsync<BusinessException>(() =>
                _fieldDefinitionAppService.UpdateAsync(fieldId, new UpdateFieldDefinitionDto
                {
                    Name = "amount",
                    DisplayName = "Amount",
                    Prompt = "Extract the amount.",
                    DataType = FieldDataType.String,   // ← 从 Number 改 String
                    DisplayOrder = 0,
                    IsRequired = false
                }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.DataTypeChangeNotAllowed);
        });
    }

    private async Task<(Guid TypeId, Guid FieldId)> SeedTypeFieldAndDocAsync(
        string typeCode, string fieldName, FieldDataType dataType, Guid docId, object value)
    {
        var type = await _documentTypeAppService.CreateAsync(new CreateDocumentTypeDto
        {
            TypeCode = typeCode,
            DisplayName = "Type",
            ConfidenceThreshold = 0.7,
            Priority = 0
        });
        var field = await _fieldDefinitionAppService.CreateAsync(new CreateFieldDefinitionDto
        {
            DocumentTypeId = type.Id,
            Name = fieldName,
            DisplayName = "Field",
            Prompt = "Extract.",
            DataType = dataType
        });

        var doc = new Document(
            docId,
            tenantId: null,
            originalFileBlobName: $"blobs/{docId:N}.pdf",
            fileOrigin: new FileOrigin("test-user", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1024, "f.pdf"));
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, type.Id);
        doc.SetFields(new[] { new DocumentFieldValue(field.Id, dataType, JsonSerializer.SerializeToElement(value)) });
        await _documentRepository.InsertAsync(doc, autoSave: true);

        return (type.Id, field.Id);
    }

    private async Task<DocumentListItemDto> GetDtoAsync(string documentTypeCode, Guid docId)
    {
        var result = await _documentAppService.GetListAsync(
            new GetDocumentListInput { DocumentTypeCode = documentTypeCode });
        return result.Items.Single(d => d.Id == docId);
    }
}
