using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Dignite.Vault.Extract.FlexFields.Tags;
using Volo.Abp.Guids;
using Volo.Abp.Validation;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Integration tests for <see cref="DocumentExportAppService.ExportAsync"/> on real EF (SQLite), #499.
/// <list type="bullet">
///   <item>the four fixed system columns always come first, followed by one column per <b>live</b> field definition of the type, in <c>DisplayOrder</c>;</item>
///   <item>typed child rows (#206) render through projection + FieldValueToString, including Number / Date / multi-value;</item>
///   <item>an <b>archived</b> field definition contributes no column, even when documents still hold values for it (#499 decision (a));</item>
///   <item>over-cap fails fast rather than truncating.</item>
/// </list>
/// Column headers come from <c>Field.DisplayName</c>; how a cell renders comes from <c>Field.FieldTypeName</c>
/// plus its configuration (#208, now #559), neither of which is persisted beside the value.
/// </summary>
// Lowers DocumentExportConsts limits for three facts; see ProcessWideStaticsCollection.
[Collection(ProcessWideStaticsCollection.Name)]
public class DocumentExport_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private const string TypeCode = "invoice.general";

    private readonly IDocumentExportAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentExport_Tests()
    {
        _appService = GetRequiredService<IDocumentExportAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Export_emits_fixed_system_columns_then_every_live_field_in_display_order()
    {
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var partnerFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            // Seeded out of DisplayOrder on purpose: Counterparty (order 1) is inserted first.
            await SeedFieldAsync(partnerFieldId, typeId, "partner", "Counterparty", TextFieldType.ControlName, displayOrder: 1);
            await SeedFieldAsync(amountFieldId, typeId, "amount", "Amount", TextFieldType.ControlName, displayOrder: 0);

            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice A", new Dictionary<string, object?> { ["amount"] = "1000", ["partner"] = "Acme" }),
                autoSave: true);
        });

        var csv = await ExportCsvAsync();

        // Columns follow DisplayOrder, not insert order — the same order the operator list renders.
        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,Amount,Counterparty");
        // #284: ReviewStatus comes from ReviewDisposition (default NotReviewed).
        // #287: ReviewReasons None -> empty cell, shown here as ",," between NotReviewed and Invoice A.
        csv.ShouldContain("Uploaded,NotReviewed,,Invoice A,1000,Acme");
    }

    [Fact]
    public async Task Export_omits_an_archived_field_definition_even_when_documents_hold_its_values()
    {
        // #499 decision (a): columns come from the type's LIVE field definitions. A document that still carries a
        // value for a soft-deleted definition exports without that column — the value is not silently relocated
        // into a neighbouring column, and no ghost header appears. The template path used to traverse soft-delete
        // to resolve columns it explicitly referenced; nothing references them now.
        var typeId = _guidGenerator.Create();
        var liveFieldId = _guidGenerator.Create();
        var archivedFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            await SeedFieldAsync(liveFieldId, typeId, "amount", "Amount", TextFieldType.ControlName, displayOrder: 0);
            await SeedFieldAsync(archivedFieldId, typeId, "legacy", "LegacyCode", TextFieldType.ControlName, displayOrder: 1);

            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice A", new Dictionary<string, object?> { ["amount"] = "1000", ["legacy"] = "LEGACY-XYZ" }),
                autoSave: true);
        });

        // Archive the definition (soft delete) after the document already holds a value for it.
        await WithUnitOfWorkAsync(async () =>
        {
            var archived = await _fieldRepository.GetAsync(archivedFieldId);
            await _fieldRepository.DeleteAsync(archived, autoSave: true);
        });

        var csv = await ExportCsvAsync();

        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,Amount");
        csv.ShouldNotContain("LegacyCode");     // no ghost header
        csv.ShouldNotContain("LEGACY-XYZ");     // and no orphaned value
        csv.ShouldContain("Uploaded,NotReviewed,,Invoice A,1000");
    }

    [Fact]
    public async Task Export_breaks_a_DisplayOrder_tie_by_field_name_so_column_order_is_total()
    {
        // #499: DisplayOrder defaults to 0, so ties are ordinary. Since the export derives its columns from
        // IFieldRepository.GetListAsync, an unstable tail would export the same data in a different
        // column order on different days — and would disagree with the operator list, which renders from the
        // same call. Name is unique per (TenantId, DocumentTypeId), so (DisplayOrder, Name) is a total order.
        var typeId = _guidGenerator.Create();
        var zebraId = _guidGenerator.Create();
        var alphaId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            // Same DisplayOrder, inserted zebra-first.
            await SeedFieldAsync(zebraId, typeId, "zebra", "Zebra", TextFieldType.ControlName);
            await SeedFieldAsync(alphaId, typeId, "alpha", "Alpha", TextFieldType.ControlName);
        });

        var csv = await ExportCsvAsync();

        csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,Alpha,Zebra");
    }

    [Fact]
    public async Task Export_exposes_MissingRequiredFields_in_the_ReviewReasons_column()
    {
        // #287: a non-blocking MissingRequiredFields document still exports. The ReviewReasons system column
        // carries the quality signal, distinct from the disposition axis ReviewStatus, which stays NotReviewed.
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            await SeedFieldAsync(amountFieldId, typeId, "amount", "Amount", TextFieldType.ControlName);
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice MRF",
                    new Dictionary<string, object?> { ["amount"] = "1000" },
                    reviewReasons: DocumentReviewReasons.MissingRequiredFields),
                autoSave: true);
        });

        var csv = await ExportCsvAsync();

        csv.ShouldContain("Uploaded,NotReviewed,MissingRequiredFields,Invoice MRF,1000");
    }

    [Fact]
    public async Task Export_renders_typed_number_and_date_fields()
    {
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var issuedFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            await SeedFieldAsync(amountFieldId, typeId, "amount", "Amount", NumberFieldType.ControlName, displayOrder: 0);
            await SeedFieldAsync(issuedFieldId, typeId, "issued", "Date", DateTimeFieldType.ControlName, DateOnly(), displayOrder: 1);
            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Invoice T", new Dictionary<string, object?> { ["amount"] = 1234.5m, ["issued"] = new DateTime(2024, 3, 9) }),
                autoSave: true);
        });

        var csv = await ExportCsvAsync();

        csv.ShouldContain("Amount,Date");
        csv.ShouldContain("1234.5");      // minimal 0.###### form: no trailing zeros from decimal(38,6)
        csv.ShouldContain("2024-03-09");
    }

    [Fact]
    public async Task Export_renders_a_multi_value_field_as_an_ordered_join()
    {
        // #212: multi-value fields join all values by ascending Order, deterministically — never relying on the DB's
        // return order for a child subquery with no explicit ordering. Rows are inserted 2,0,1 on purpose.
        var typeId = _guidGenerator.Create();
        var tagsFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            await SeedFieldAsync(tagsFieldId, typeId, "tags", "Tags", TagsFieldType.ControlName);

            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Doc M", new Dictionary<string, object?> { ["tags"] = new List<string> { "urgent", "legal", "2026" } }),
                autoSave: true);
        });

        var csv = await ExportCsvAsync();

        csv.ShouldContain("Doc M,urgent; legal; 2026");
    }

    [Fact]
    public async Task Export_fails_fast_over_the_cap_instead_of_truncating()
    {
        var originalMax = DocumentExportConsts.MaxExportDocumentCount;
        DocumentExportConsts.MaxExportDocumentCount = 2;
        try
        {
            var typeId = _guidGenerator.Create();
            await WithUnitOfWorkAsync(async () =>
            {
                await SeedTypeAsync(typeId);
                for (var i = 0; i < 3; i++)
                {
                    await _documentRepository.InsertAsync(
                        CreateDocument(_guidGenerator.Create(), typeId, $"Doc {i}", fields: null), autoSave: true);
                }
            });

            await WithUnitOfWorkAsync(async () =>
            {
                var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ExportAsync(NewInput()));
                ex.Code.ShouldBe(VaultExtractErrorCodes.Export.DocumentLimitExceeded);
            });
        }
        finally
        {
            DocumentExportConsts.MaxExportDocumentCount = originalMax;
        }
    }

    [Fact]
    public async Task Export_fails_fast_when_the_type_declares_more_fields_than_the_column_cap()
    {
        // #501 item 2: #499 derived the columns from the type's live fields and deleted the template layer that
        // used to cap them at 100, leaving rows bounded and columns unbounded. Three fields, cap of two.
        var originalMax = DocumentExportConsts.MaxColumnCount;
        DocumentExportConsts.MaxColumnCount = 2;
        try
        {
            var typeId = _guidGenerator.Create();
            await WithUnitOfWorkAsync(async () =>
            {
                await SeedTypeAsync(typeId);
                await SeedFieldAsync(_guidGenerator.Create(), typeId, "a", "A", TextFieldType.ControlName, displayOrder: 0);
                await SeedFieldAsync(_guidGenerator.Create(), typeId, "b", "B", TextFieldType.ControlName, displayOrder: 1);
                await SeedFieldAsync(_guidGenerator.Create(), typeId, "c", "C", TextFieldType.ControlName, displayOrder: 2);
            });

            await WithUnitOfWorkAsync(async () =>
            {
                var ex = await Should.ThrowAsync<BusinessException>(() => _appService.ExportAsync(NewInput()));
                ex.Code.ShouldBe(VaultExtractErrorCodes.Export.ColumnLimitExceeded);
            });
        }
        finally
        {
            DocumentExportConsts.MaxColumnCount = originalMax;
        }
    }

    [Fact]
    public async Task Export_allows_a_type_declaring_exactly_the_column_cap()
    {
        // The bound is inclusive. Pinned separately because an off-by-one here rejects a legal export, and the
        // fail-fast test above passes either way.
        var originalMax = DocumentExportConsts.MaxColumnCount;
        DocumentExportConsts.MaxColumnCount = 2;
        try
        {
            var typeId = _guidGenerator.Create();
            await WithUnitOfWorkAsync(async () =>
            {
                await SeedTypeAsync(typeId);
                await SeedFieldAsync(_guidGenerator.Create(), typeId, "a", "A", TextFieldType.ControlName, displayOrder: 0);
                await SeedFieldAsync(_guidGenerator.Create(), typeId, "b", "B", TextFieldType.ControlName, displayOrder: 1);
            });

            var csv = await ExportCsvAsync();

            // The four fixed system columns do not count against the cap; only the type-bound fields do.
            csv.ShouldContain("LifecycleStatus,ReviewStatus,ReviewReasons,Title,A,B");
        }
        finally
        {
            DocumentExportConsts.MaxColumnCount = originalMax;
        }
    }

    [Fact]
    public async Task Export_keeps_each_fields_values_in_its_own_column()
    {
        // #501 item 3 replaced the per-cell rescan of every value the document holds with one ILookup per row.
        // A bucket keyed or sorted wrongly would smear one field's values into another's column, or lose the
        // ascending-Order join that Export_renders_a_multi_value_field_as_an_ordered_join pins.
        var typeId = _guidGenerator.Create();
        var tagsFieldId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            await SeedFieldAsync(tagsFieldId, typeId, "tags", "Tags", TagsFieldType.ControlName, displayOrder: 0);
            await SeedFieldAsync(amountFieldId, typeId, "amount", "Amount", TextFieldType.ControlName, displayOrder: 1);

            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Doc X", new Dictionary<string, object?> { ["amount"] = "42", ["tags"] = new List<string> { "alpha", "beta" } }),
                autoSave: true);
        });

        var csv = await ExportCsvAsync();

        // Tags joins its own two rows in Order; Amount carries only its own value, never a neighbour's.
        csv.ShouldContain("Doc X,alpha; beta,42");
    }

    [Fact]
    public async Task Export_leaves_an_empty_cell_for_a_field_the_document_has_no_value_for()
    {
        // The ILookup indexer must yield an empty bucket (not throw, not borrow another field's rows) for a field
        // this document never had extracted.
        var typeId = _guidGenerator.Create();
        var amountFieldId = _guidGenerator.Create();
        var missingFieldId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await SeedTypeAsync(typeId);
            await SeedFieldAsync(amountFieldId, typeId, "amount", "Amount", TextFieldType.ControlName, displayOrder: 0);
            await SeedFieldAsync(missingFieldId, typeId, "absent", "Absent", TextFieldType.ControlName, displayOrder: 1);

            await _documentRepository.InsertAsync(
                CreateDocument(_guidGenerator.Create(), typeId, "Doc Y", new Dictionary<string, object?> { ["amount"] = "7" }),
                autoSave: true);
        });

        var csv = await ExportCsvAsync();

        csv.ShouldContain("Doc Y,7,");
    }

    [Fact]
    public async Task Export_loud_fails_on_an_unknown_document_type_rather_than_emitting_an_empty_file()
    {
        // A header-only CSV would be a silent lie about what the layer contains. The list may legitimately show an
        // empty page for an unknown type; an artifact handed to an accountant may not.
        await Should.ThrowAsync<EntityNotFoundException>(() =>
            _appService.ExportAsync(new ExportDocumentsInput { DocumentTypeCode = "no.such.type" }));
    }

    [Fact]
    public async Task Export_rejects_an_undefined_format_rather_than_silently_writing_csv()
    {
        // System.Text.Json casts a JSON number straight onto the enum with no range check, and both format switches
        // fall through to CSV. Without [EnumDataType], `{"format": 99}` would answer 200 + a CSV — telling a caller
        // that a format we do not have was produced.
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => SeedTypeAsync(typeId));

        await Should.ThrowAsync<AbpValidationException>(() =>
            _appService.ExportAsync(NewInput(i => i.Format = (ExportFormat)99)));
    }

    [Fact]
    public async Task Export_names_the_file_after_the_type_code_and_format()
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => SeedTypeAsync(typeId));

        await WithUnitOfWorkAsync(async () =>
        {
            var csv = await _appService.ExportAsync(NewInput());
            csv.FileName.ShouldStartWith(TypeCode + "-");
            csv.FileName.ShouldEndWith(".csv");

            var xlsx = await _appService.ExportAsync(NewInput(i => i.Format = ExportFormat.Xlsx));
            xlsx.FileName.ShouldEndWith(".xlsx");
        });
    }

    private static ExportDocumentsInput NewInput(Action<ExportDocumentsInput>? configure = null)
    {
        var input = new ExportDocumentsInput { DocumentTypeCode = TypeCode };
        configure?.Invoke(input);
        return input;
    }

    private async Task<string> ExportCsvAsync()
    {
        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _appService.ExportAsync(NewInput());
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });
        return csv;
    }

    private Task SeedTypeAsync(Guid typeId) =>
        _documentTypeRepository.InsertAsync(new DocumentType(typeId, null, TypeCode, "Invoice"), autoSave: true);

    private Task SeedFieldAsync(
        Guid fieldId,
        Guid typeId,
        string name,
        string displayName,
        string fieldTypeName,
        FieldConfigurationDictionary? configuration = null,
        int displayOrder = 0) =>
        _fieldRepository.InsertAsync(
            new Field(
                fieldId, null, typeId, name, displayName, fieldTypeName,
                description: "extract", configuration: configuration, displayOrder: displayOrder),
            autoSave: true);

    private static Document CreateDocument(
        Guid id,
        Guid documentTypeId,
        string title,
        IReadOnlyDictionary<string, object?>? fields,
        DocumentReviewReasons reviewReasons = DocumentReviewReasons.None)
    {
        var document = new Document(id, tenantId: null, DocumentTestData.NewFileOrigin(id, originalFileName: "f.pdf"));

        DocumentTestData.MarkClassified(document, documentTypeId);
        DocumentTestData.SetTitle(document, title);

        if (reviewReasons != DocumentReviewReasons.None)
        {
            document.SetReviewReason(reviewReasons, present: true);
        }

        if (fields != null)
        {
            document.SetFlexFields(fields);
        }

        return document;
    }

    private static FieldConfigurationDictionary DateOnly() =>
        new DateTimeConfiguration { InputMode = DateTimeInputMode.Date }.ConfigurationDictionary;
}
