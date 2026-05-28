using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Exports;
using Dignite.Paperbase.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// ExportTemplateAppService.ExportAsync 集成测试（SQLite 真实 EF，#207）：
/// <list type="bullet">
///   <item>固定系统字段（SourceType / LifecycleStatus / ReviewStatus / Title）始终在前，模板抽取列（按 FieldDefinitionId 匹配）在后</item>
///   <item>typed child 行（#206）经投影 + FieldValueToString 正确渲染（含 Number / Date）</item>
///   <item>over-cap fail-fast（fetch Max+1，超限抛错而非静默截断）</item>
/// </list>
/// 注：导出按 FieldDefinitionId 匹配列、渲染存储的 ColumnName；字段类型由 FieldDefinition.DataType 决定（#208，
/// 不在字段值行持久化），故导出会按模板列 join FieldDefinition 取 DataType——本测试经 SeedSchemaAsync seed 这些字段定义行。
/// </summary>
public class ExportTemplateExport_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private readonly IExportTemplateAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IExportTemplateRepository _templateRepository;
    private readonly IGuidGenerator _guidGenerator;

    public ExportTemplateExport_Tests()
    {
        _appService = GetRequiredService<IExportTemplateAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _templateRepository = GetRequiredService<IExportTemplateRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    // FK RESTRICT 真实生效（#207）：Document.DocumentTypeId / ExportTemplate.DocumentTypeId → DocumentType，
    // DocumentExtractedField.FieldDefinitionId → FieldDefinition。插入前先 seed 父行（ExportColumn 内的 FieldDefinitionId
    // 在序列化 JSON 内无 FK，但文档字段值有，故按文档字段值 seed）。
    private async Task SeedSchemaAsync(Guid typeId, params DocumentFieldValue[] fields)
    {
        await _documentTypeRepository.InsertAsync(
            new DocumentType(typeId, null, "t" + typeId.ToString("N"), "Type"), autoSave: true);
        foreach (var f in fields)
        {
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    f.FieldDefinitionId, null, typeId,
                    name: "f" + f.FieldDefinitionId.ToString("N"),
                    displayName: "field", prompt: "extract", dataType: f.DataType),
                autoSave: true);
        }
    }

    [Fact]
    public async Task Export_Should_Emit_Fixed_System_Fields_Then_Extracted_Columns()
    {
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var partnerFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var fields = new[]
            {
                new DocumentFieldValue(amountFieldId, FieldDataType.String, Json("1000")),
                new DocumentFieldValue(partnerFieldId, FieldDataType.String, Json("Acme")),
            };
            await SeedSchemaAsync(typeId, fields);
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice A", fields),
                autoSave: true);

            await _templateRepository.InsertAsync(
                new ExportTemplate(
                    templateId,
                    tenantId: null,
                    name: "Invoice Export",
                    format: ExportFormat.Csv,
                    documentTypeId: typeId,
                    new[]
                    {
                        new ExportColumn(amountFieldId, "金额", 0),
                        new ExportColumn(partnerFieldId, "对方", 1),
                    }),
                autoSave: true);
        });

        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId });
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });

        // 固定系统字段列在前（SourceType / LifecycleStatus / ReviewStatus / Title），模板抽取列在后。
        csv.ShouldContain("SourceType,LifecycleStatus,ReviewStatus,Title,金额,对方");
        // 新建文档默认 LifecycleStatus=Uploaded、ReviewStatus=None、SourceType=Digital。
        csv.ShouldContain("Digital,Uploaded,None,Invoice A,1000,Acme");
    }

    [Fact]
    public async Task Export_Should_Render_Typed_Number_And_Date_Fields()
    {
        // 覆盖非 String 字段经 typed child 投影 + FieldValueToString 的导出渲染。
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var issuedFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var fields = new[]
            {
                new DocumentFieldValue(amountFieldId, FieldDataType.Number, JsonSerializer.SerializeToElement(1234.5m)),
                new DocumentFieldValue(issuedFieldId, FieldDataType.Date, JsonSerializer.SerializeToElement("2024-03-09")),
            };
            await SeedSchemaAsync(typeId, fields);
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice T", fields),
                autoSave: true);

            await _templateRepository.InsertAsync(
                new ExportTemplate(
                    templateId,
                    tenantId: null,
                    name: "Typed Invoice",
                    format: ExportFormat.Csv,
                    documentTypeId: typeId,
                    new[]
                    {
                        new ExportColumn(amountFieldId, "金额", 0),
                        new ExportColumn(issuedFieldId, "日期", 1),
                    }),
                autoSave: true);
        });

        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId });
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });

        csv.ShouldContain("金额,日期");
        csv.ShouldContain("1234.5");      // Number 渲染（0.###### 最小形：1234.5m → "1234.5"，无尾随零）
        csv.ShouldContain("2024-03-09");  // Date 渲染
    }

    [Fact]
    public async Task Export_Should_Fail_When_Over_Cap_Instead_Of_Truncating()
    {
        var originalMax = ExportTemplateConsts.MaxExportDocumentCount;
        ExportTemplateConsts.MaxExportDocumentCount = 2;
        try
        {
            var templateId = _guidGenerator.Create();
            var typeId = _guidGenerator.Create();

            await WithUnitOfWorkAsync(async () =>
            {
                await SeedSchemaAsync(typeId);   // 文档无字段值，仅需 seed 父类型供 Document/Template FK。
                for (var i = 0; i < 3; i++)
                {
                    await _documentRepository.InsertAsync(
                        CreateDocument(_guidGenerator.Create(), typeId, $"Doc {i}", fields: null),
                        autoSave: true);
                }

                await _templateRepository.InsertAsync(
                    new ExportTemplate(
                        templateId,
                        tenantId: null,
                        name: "Capped",
                        format: ExportFormat.Csv,
                        documentTypeId: typeId,
                        new[] { new ExportColumn(_guidGenerator.Create(), "T", 0) }),
                    autoSave: true);
            });

            await WithUnitOfWorkAsync(async () =>
            {
                var ex = await Should.ThrowAsync<BusinessException>(() =>
                    _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId }));
                ex.Code.ShouldBe(PaperbaseErrorCodes.ExportDocumentLimitExceeded);
            });
        }
        finally
        {
            ExportTemplateConsts.MaxExportDocumentCount = originalMax;
        }
    }

    [Fact]
    public async Task Template_Crud_Resolves_FieldName_To_FieldDefinitionId_And_Back()
    {
        // #207：模板列以 FieldName 提交 → AppService 按 (DocumentTypeId, name) 解析为 FieldDefinitionId 持久化；
        // 读回时 join 当前 FieldDefinition.Name。覆盖 MapColumnsAsync + FillTemplateDtosAsync。
        var typeId = _guidGenerator.Create();
        var fieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, null, "contract.general", "Contract"), autoSave: true);
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(fieldId, null, typeId, "amount", "Amount", "extract", FieldDataType.Number),
                autoSave: true);
        });

        Guid templateId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var created = await _appService.CreateAsync(new CreateExportTemplateDto
            {
                Name = "Contract Export",
                Format = ExportFormat.Csv,
                DocumentTypeCode = "contract.general",
                Columns = new List<ExportColumnInput>
                {
                    new() { FieldName = "amount", ColumnName = "金额", Order = 0 }
                }
            });

            templateId = created.Id;
            created.DocumentTypeCode.ShouldBe("contract.general");
            created.Columns.ShouldHaveSingleItem();
            created.Columns[0].FieldDefinitionId.ShouldBe(fieldId);   // 解析为内部 Id
            created.Columns[0].FieldName.ShouldBe("amount");          // join 回当前名
            created.Columns[0].ColumnName.ShouldBe("金额");
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var got = await _appService.GetAsync(templateId);
            got.DocumentTypeCode.ShouldBe("contract.general");
            got.Columns[0].FieldDefinitionId.ShouldBe(fieldId);
            got.Columns[0].FieldName.ShouldBe("amount");
        });
    }

    [Fact]
    public async Task Template_Create_Rejects_Unknown_FieldName()
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => _documentTypeRepository.InsertAsync(
            new DocumentType(typeId, null, "contract.general", "Contract"), autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            // 该类型下无 "ghost" 字段 → loud fail（UnknownExtractedField）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _appService.CreateAsync(new CreateExportTemplateDto
            {
                Name = "Bad",
                Format = ExportFormat.Csv,
                DocumentTypeCode = "contract.general",
                Columns = new List<ExportColumnInput>
                {
                    new() { FieldName = "ghost", ColumnName = "X", Order = 0 }
                }
            }));
            ex.Code.ShouldBe(PaperbaseErrorCodes.UnknownExtractedField);
        });
    }

    private static Document CreateDocument(
        Guid id,
        Guid documentTypeId,
        string title,
        IEnumerable<DocumentFieldValue>? fields)
    {
        var document = new Document(
            id,
            tenantId: null,
            originalFileBlobName: $"blobs/{id:N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "f.pdf"));

        // DocumentTypeId / Title 为 private setter——测试用反射模拟"已分类 + 已提取标题"。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId);
        typeof(Document).GetProperty(nameof(Document.Title))!.SetValue(document, title);

        if (fields != null)
        {
            document.SetFields(fields);
        }

        return document;
    }

    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }
}
