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
using Volo.Abp.Domain.Entities;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// ExportTemplateAppService.ExportAsync 集成测试（SQLite 真实 EF，#207）：
/// <list type="bullet">
///   <item>固定系统字段（LifecycleStatus / ReviewStatus / ReviewReasons / Title）始终在前，模板抽取列（按 FieldDefinitionId 匹配）在后</item>
///   <item>typed child 行（#206）经投影 + FieldValueToString 正确渲染（含 Number / Date）</item>
///   <item>over-cap fail-fast（fetch Max+1，超限抛错而非静默截断）</item>
/// </list>
/// 注：导出按 FieldDefinitionId 匹配列，列标题取 FieldDefinition.DisplayName；字段类型由 FieldDefinition.DataType 决定（#208，
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
    private async Task SeedSchemaAsync(Guid typeId, params (DocumentFieldValue Field, string DisplayName)[] columns)
    {
        await _documentTypeRepository.InsertAsync(
            new DocumentType(typeId, null, "t" + typeId.ToString("N"), "Type"), autoSave: true);
        foreach (var (f, displayName) in columns)
        {
            // #207：导出列标题取 FieldDefinition.DisplayName（不在 ExportColumn 上配置）——seed 真实 DisplayName 以验证表头映射。
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    f.FieldDefinitionId, null, typeId,
                    name: "f" + f.FieldDefinitionId.ToString("N"),
                    displayName: displayName, prompt: "extract", dataType: f.DataType),
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
            var amount = new DocumentFieldValue(amountFieldId, FieldDataType.Text, Json("1000"));
            var partner = new DocumentFieldValue(partnerFieldId, FieldDataType.Text, Json("Acme"));
            await SeedSchemaAsync(typeId, (amount, "金额"), (partner, "对方"));
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice A", new[] { amount, partner }),
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
                        new ExportColumn(amountFieldId, 0),
                        new ExportColumn(partnerFieldId, 1),
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

        // 固定系统字段列在前（LifecycleStatus / ReviewStatus / ReviewReasons / Title），模板抽取列在后。
        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,金额,对方");
        // #284：ReviewStatus 列值取 ReviewDisposition（DB 列名保持 "ReviewStatus" 不变以稳定导出 schema），默认 NotReviewed。
        // #287：ReviewReasons 列 None → 空单元格（此处即 NotReviewed 与 Invoice A 之间的 ",,"）。
        // UC（分类未定）文档 DocumentTypeId=null，被类型绑定导出过滤掉、不会出现在此处。
        csv.ShouldContain("Uploaded,NotReviewed,,Invoice A,1000,Acme");
    }

    [Fact]
    public async Task Export_Exposes_MissingRequiredFields_In_ReviewReasons_Column()
    {
        // #287：non-blocking 的 MissingRequiredFields 文档照常进类型绑定导出（DocumentTypeId 非空、Ready）。
        // ReviewReasons 系统列透出 "MissingRequiredFields" 质量信号，区别于处置轴 ReviewStatus（仍是 NotReviewed）。
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var amount = new DocumentFieldValue(amountFieldId, FieldDataType.Text, Json("1000"));
            await SeedSchemaAsync(typeId, (amount, "金额"));
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice MRF", new[] { amount },
                    reviewReasons: DocumentReviewReasons.MissingRequiredFields),
                autoSave: true);

            await _templateRepository.InsertAsync(
                new ExportTemplate(
                    templateId, tenantId: null, name: "MRF Export", format: ExportFormat.Csv,
                    documentTypeId: typeId,
                    new[] { new ExportColumn(amountFieldId, 0) }),
                autoSave: true);
        });

        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId });
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });

        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,金额");
        // ReviewReasons 列 = "MissingRequiredFields"（处置轴 ReviewStatus 仍是 NotReviewed）。
        csv.ShouldContain("Uploaded,NotReviewed,MissingRequiredFields,Invoice MRF,1000");
    }

    [Fact]
    public async Task Export_Should_Render_Typed_Number_And_Date_Fields()
    {
        // 覆盖非文本字段经 typed child 投影 + FieldValueToString 的导出渲染。
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var issuedFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var amount = new DocumentFieldValue(amountFieldId, FieldDataType.Number, JsonSerializer.SerializeToElement(1234.5m));
            var issued = new DocumentFieldValue(issuedFieldId, FieldDataType.Date, JsonSerializer.SerializeToElement("2024-03-09"));
            await SeedSchemaAsync(typeId, (amount, "金额"), (issued, "日期"));
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice T", new[] { amount, issued }),
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
                        new ExportColumn(amountFieldId, 0),
                        new ExportColumn(issuedFieldId, 1),
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
    public async Task Export_Renders_MultiValue_Field_As_Ordered_Join()
    {
        // #212：多值字段作为导出列 → 按 Order 升序 join 全部值（不丢值、确定，不依赖 DB 对 child 子查询未指定的
        // 行返回顺序）。本测试故意把行按 Order 2,0,1 的乱序插入——验证导出仍按 Order 0,1,2 = "urgent; legal; 2026"。
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var tagsFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, null, "t" + typeId.ToString("N"), "Type"), autoSave: true);
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    tagsFieldId, null, typeId, "tags", "Tags", "extract",
                    FieldDataType.Text, allowMultiple: true),
                autoSave: true);

            // 乱序插入：物理首行是 Order 2（"2026"），Order 0（"urgent"）在中间。
            var fields = new[]
            {
                new DocumentFieldValue(tagsFieldId, FieldDataType.Text, Json("2026"), 2),
                new DocumentFieldValue(tagsFieldId, FieldDataType.Text, Json("urgent"), 0),
                new DocumentFieldValue(tagsFieldId, FieldDataType.Text, Json("legal"), 1),
            };
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Doc M", fields),
                autoSave: true);

            await _templateRepository.InsertAsync(
                new ExportTemplate(
                    templateId, tenantId: null, name: "Tags Export", format: ExportFormat.Csv,
                    documentTypeId: typeId,
                    new[] { new ExportColumn(tagsFieldId, 0) }),
                autoSave: true);
        });

        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId });
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });

        // 按 Order 0,1,2 join（"urgent; legal; 2026"），不是乱序物理顺序 "2026; urgent; legal"。
        csv.ShouldContain("Doc M,urgent; legal; 2026");
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
                        new[] { new ExportColumn(_guidGenerator.Create(), 0) }),
                    autoSave: true);
            });

            await WithUnitOfWorkAsync(async () =>
            {
                var ex = await Should.ThrowAsync<BusinessException>(() =>
                    _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId }));
                ex.Code.ShouldBe(PaperbaseErrorCodes.Export.DocumentLimitExceeded);
            });
        }
        finally
        {
            ExportTemplateConsts.MaxExportDocumentCount = originalMax;
        }
    }

    [Fact]
    public async Task Template_Crud_Roundtrips_DocumentTypeId_And_FieldDefinitionId()
    {
        // #207：模板以不可变 DocumentTypeId / 列 FieldDefinitionId 提交 → AppService 校验字段属于该类型后持久化；
        // 读回由 Mapperly 直接映射回 Id。覆盖 EnsureDocumentTypeExistsAsync + MapColumnsAsync。
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
                DocumentTypeId = typeId,
                Columns = new List<ExportColumnInput>
                {
                    new() { FieldDefinitionId = fieldId, Order = 0 }
                }
            });

            templateId = created.Id;
            created.DocumentTypeId.ShouldBe(typeId);
            created.Columns.ShouldHaveSingleItem();
            created.Columns[0].FieldDefinitionId.ShouldBe(fieldId);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var got = await _appService.GetAsync(templateId);
            got.DocumentTypeId.ShouldBe(typeId);
            got.Columns[0].FieldDefinitionId.ShouldBe(fieldId);
        });
    }

    [Fact]
    public async Task Template_Create_Rejects_Unknown_FieldDefinitionId()
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => _documentTypeRepository.InsertAsync(
            new DocumentType(typeId, null, "contract.general", "Contract"), autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            // 该类型下无此 FieldDefinitionId → loud fail（EntityNotFoundException）。
            await Should.ThrowAsync<EntityNotFoundException>(() => _appService.CreateAsync(new CreateExportTemplateDto
            {
                Name = "Bad",
                Format = ExportFormat.Csv,
                DocumentTypeId = typeId,
                Columns = new List<ExportColumnInput>
                {
                    new() { FieldDefinitionId = _guidGenerator.Create(), Order = 0 }
                }
            }));
        });
    }

    private static Document CreateDocument(
        Guid id,
        Guid documentTypeId,
        string title,
        IEnumerable<DocumentFieldValue>? fields,
        DocumentReviewReasons reviewReasons = DocumentReviewReasons.None)
    {
        var document = new Document(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "f.pdf"));

        // DocumentTypeId / Title 为 private setter——测试用反射模拟"已分类 + 已提取标题"。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId);
        typeof(Document).GetProperty(nameof(Document.Title))!.SetValue(document, title);

        if (reviewReasons != DocumentReviewReasons.None)
        {
            // #287：模拟字段抽取阶段在已分类文档上物化的 non-blocking 原因（如 MissingRequiredFields）。
            document.SetReviewReason(reviewReasons, present: true);
        }

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
