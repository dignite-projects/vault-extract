using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Integration tests for ExportTemplateAppService.ExportAsync using real EF on SQLite (#207):
/// <list type="bullet">
///   <item>fixed system fields (LifecycleStatus / ReviewStatus / ReviewReasons / Title) always come first, with template extracted columns matched by FieldDefinitionId after them.</item>
///   <item>typed child rows (#206) render correctly through projection + FieldValueToString, including Number / Date.</item>
///   <item>over-cap fail-fast behavior fetches Max+1 and throws on limit overflow instead of silently truncating.</item>
/// </list>
/// Note: export matches columns by FieldDefinitionId, and column titles come from FieldDefinition.DisplayName.
/// Field type is decided by FieldDefinition.DataType (#208) and is not persisted in field-value rows, so export
/// joins FieldDefinition for each template column to get DataType. SeedSchemaAsync seeds those field definition rows.
/// </summary>
public class ExportTemplateExport_Tests : ExtractEntityFrameworkCoreTestBase
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

    // FK RESTRICT is truly enforced (#207): Document.DocumentTypeId / ExportTemplate.DocumentTypeId -> DocumentType,
    // and DocumentExtractedField.FieldDefinitionId -> FieldDefinition. Seed parent rows before insertion.
    // FieldDefinitionId inside ExportColumn is serialized JSON without an FK, but document field values do have
    // one, so seed according to document field values.
    private async Task SeedSchemaAsync(Guid typeId, params (DocumentFieldValue Field, string DisplayName)[] columns)
    {
        await _documentTypeRepository.InsertAsync(
            new DocumentType(typeId, null, "t" + typeId.ToString("N"), "Type"), autoSave: true);
        foreach (var (f, displayName) in columns)
        {
            // #207: export column titles come from FieldDefinition.DisplayName, not ExportColumn. Seed real
            // DisplayName values to verify header mapping.
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
            await SeedSchemaAsync(typeId, (amount, "Amount"), (partner, "Counterparty"));
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

        // Fixed system fields come first (LifecycleStatus / ReviewStatus / ReviewReasons / Title), followed by
        // template extracted columns.
        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,Amount,Counterparty");
        // #284: ReviewStatus column value comes from ReviewDisposition. DB column name remains "ReviewStatus" to
        // keep export schema stable; default is NotReviewed.
        // #287: ReviewReasons None -> empty cell, shown here as ",," between NotReviewed and Invoice A.
        // UC (unclassified) documents with DocumentTypeId=null are filtered out by type-bound export.
        csv.ShouldContain("Uploaded,NotReviewed,,Invoice A,1000,Acme");
    }

    [Fact]
    public async Task Export_Exposes_MissingRequiredFields_In_ReviewReasons_Column()
    {
        // #287: non-blocking MissingRequiredFields documents still enter type-bound export when DocumentTypeId is
        // non-null and Ready. The ReviewReasons system column exposes the "MissingRequiredFields" quality signal,
        // distinct from the disposition axis ReviewStatus, which is still NotReviewed.
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var amount = new DocumentFieldValue(amountFieldId, FieldDataType.Text, Json("1000"));
            await SeedSchemaAsync(typeId, (amount, "Amount"));
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

        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,Amount");
        // ReviewReasons column = "MissingRequiredFields"; disposition-axis ReviewStatus is still NotReviewed.
        csv.ShouldContain("Uploaded,NotReviewed,MissingRequiredFields,Invoice MRF,1000");
    }

    [Fact]
    public async Task Export_Should_Render_Typed_Number_And_Date_Fields()
    {
        // Covers export rendering for non-text fields through typed child projection + FieldValueToString.
        var templateId = _guidGenerator.Create();
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var issuedFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var amount = new DocumentFieldValue(amountFieldId, FieldDataType.Number, JsonSerializer.SerializeToElement(1234.5m));
            var issued = new DocumentFieldValue(issuedFieldId, FieldDataType.Date, JsonSerializer.SerializeToElement("2024-03-09"));
            await SeedSchemaAsync(typeId, (amount, "Amount"), (issued, "Date"));
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

        csv.ShouldContain("Amount,Date");
        csv.ShouldContain("1234.5");      // Number rendering uses minimal 0.###### form: 1234.5m -> "1234.5", no trailing zero.
        csv.ShouldContain("2024-03-09");  // Date rendering.
    }

    [Fact]
    public async Task Export_Renders_MultiValue_Field_As_Ordered_Join()
    {
        // #212: multi-value fields as export columns join all values by ascending Order. This is complete,
        // deterministic, and does not rely on DB return order for child subqueries without explicit ordering.
        // This test intentionally inserts rows in Order 2,0,1 and verifies export still uses
        // Order 0,1,2 = "urgent; legal; 2026".
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

            // Out-of-order insert: the physical first row is Order 2 ("2026"), with Order 0 ("urgent") in the middle.
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

        // Join by Order 0,1,2 ("urgent; legal; 2026"), not physical out-of-order order "2026; urgent; legal".
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
                await SeedSchemaAsync(typeId);   // Document has no field values; only seed parent type for Document / Template FK.
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
                ex.Code.ShouldBe(ExtractErrorCodes.Export.DocumentLimitExceeded);
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
        // #207: templates are submitted with immutable DocumentTypeId / column FieldDefinitionId. AppService
        // validates fields belong to that type before persistence; readback maps Ids directly through Mapperly.
        // Covers EnsureDocumentTypeExistsAsync + MapColumnsAsync.
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
            // This type does not contain that FieldDefinitionId -> loud fail (EntityNotFoundException).
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

        // DocumentTypeId / Title have private setters; tests use reflection to simulate "classified + title extracted".
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId);
        typeof(Document).GetProperty(nameof(Document.Title))!.SetValue(document, title);

        if (reviewReasons != DocumentReviewReasons.None)
        {
            // #287: simulate non-blocking reasons materialized by field extraction on a classified document,
            // such as MissingRequiredFields.
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
