using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Content;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.Exports;

/// <summary>
/// #414 part 3: real EF (SQLite) integration coverage that <see cref="ExportTemplateAppService.ExportAsync"/>
/// narrows by extracted-field-value filters — the same <c>DocumentFieldFilter</c> the operator list / MCP
/// search use, resolved through the shared <see cref="DocumentFieldQueryResolver"/> and matched via
/// <c>GetFieldMatchedIdsAsync</c>. The per-type matching mechanics themselves are covered by
/// <c>EfCoreDocumentRepositorySearch_Tests</c>; here we verify the export wiring + the unknown-field loud-fail.
/// </summary>
public class ExportTemplateAppService_FieldFilter_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private const string TypeCode = "invoice.general";

    private readonly IExportTemplateAppService _exportAppService;
    private readonly IExportTemplateRepository _templateRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICurrentTenant _currentTenant;

    public ExportTemplateAppService_FieldFilter_Tests()
    {
        _exportAppService = GetRequiredService<IExportTemplateAppService>();
        _templateRepository = GetRequiredService<IExportTemplateRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task Export_narrows_documents_by_field_filter()
    {
        var templateId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m);
            await SeedDocumentAsync(200m);
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "amount", Value = "100" } },
        });

        // Only the amount=100 document is exported; the amount=200 one is filtered out.
        csv.ShouldContain("100");
        csv.ShouldNotContain("200");
    }

    [Fact]
    public async Task Export_without_field_filter_includes_all_type_documents()
    {
        var templateId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedDocumentAsync(100m);
            await SeedDocumentAsync(200m);
            await SeedTemplateAsync(templateId);
        });

        var csv = await ExportCsvAsync(new ExportDocumentsInput { TemplateId = templateId });

        // Baseline: no field filter → both documents exported (proves the filter above is what narrowed it).
        csv.ShouldContain("100");
        csv.ShouldContain("200");
    }

    [Fact]
    public async Task Export_with_unknown_field_loud_fails()
    {
        var templateId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await SeedSchemaAsync();
            await SeedTemplateAsync(templateId);
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _exportAppService.ExportAsync(new ExportDocumentsInput
        {
            TemplateId = templateId,
            FieldFilters = new List<DocumentFieldFilter> { new() { Name = "ghost", Value = "x" } },
        }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.ExtractedField.Unknown);
    }

    private async Task<string> ExportCsvAsync(ExportDocumentsInput input)
    {
        var content = await _exportAppService.ExportAsync(input);
        using var reader = new StreamReader(content.GetStream());
        return await reader.ReadToEndAsync();
    }

    private async Task SeedSchemaAsync()
    {
        await _documentTypeRepository.InsertAsync(
            new DocumentType(TypeId, _currentTenant.Id, TypeCode, TypeCode), autoSave: true);
        await _fieldDefinitionRepository.InsertAsync(
            new FieldDefinition(
                AmountFieldId, _currentTenant.Id, TypeId,
                name: "amount", displayName: "Amount", prompt: null, dataType: FieldDataType.Number),
            autoSave: true);
    }

    private async Task SeedDocumentAsync(decimal amount)
    {
        var id = Guid.NewGuid();
        var doc = new Document(
            id,
            _currentTenant.Id,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "invoice.pdf"));

        // DocumentTypeId has a Domain private setter; simulate the classified state (#207 internal relation by Id).
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(doc, TypeId);
        doc.SetFields(new[]
        {
            new DocumentFieldValue(AmountFieldId, FieldDataType.Number, JsonSerializer.SerializeToElement(amount)),
        });

        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    private async Task SeedTemplateAsync(Guid templateId)
    {
        await _templateRepository.InsertAsync(
            new ExportTemplate(
                templateId, _currentTenant.Id, "invoices", ExportFormat.Csv, TypeId,
                new List<ExportColumn> { new(AmountFieldId, 0) }),
            autoSave: true);
    }

    private static Guid TypeId => DeterministicGuid("type:" + TypeCode);
    private static Guid AmountFieldId => DeterministicGuid("field:amount");
    private static Guid DeterministicGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
