using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Boolean;
using Dignite.Abp.FlexFields.CKEditor;
using Dignite.Abp.FlexFields.Date;
using Dignite.Abp.FlexFields.Number;
using Dignite.Abp.FlexFields.Table;
using Dignite.Abp.FlexFields.Text;
using Dignite.Vault.Extract.FlexFields.Tags;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.EntityFrameworkCore.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes.Packs;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Data;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents.DocumentTypes;

/// <summary>
/// #444 config pack round-trip: exercises <see cref="IDocumentTypePackAppService"/> against the real SQLite
/// DB + real repositories/managers. Covers create-from-pack, export↔import round-trip, idempotent re-import
/// (no duplicates), CreateOnly additive semantics, and up-front version rejection with no partial writes.
/// </summary>
public class DocumentTypePackAppService_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentTypePackAppService _packAppService;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldDefinitionRepository;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly VaultExtractBehaviorOptions _behaviorOptions;

    public DocumentTypePackAppService_Tests()
    {
        _packAppService = GetRequiredService<IDocumentTypePackAppService>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldRepository>();
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        _behaviorOptions = GetRequiredService<IOptions<VaultExtractBehaviorOptions>>().Value;
    }

    private static DocumentTypePackDto SamplePack(string typeCode = "host.invoice") => new()
    {
        Version = DocumentTypePackConsts.CurrentVersion,
        TypeCode = typeCode,
        DisplayName = "Invoice",
        Description = "Invoice documents",
        ConfidenceThreshold = 0.8,
        Priority = 5,
        Fields = new List<DocumentTypePackFieldDto>
        {
            new() { Name = "amount", DisplayName = "Amount", Description = "the total", FieldTypeName = NumberFieldType.ControlName, DisplayOrder = 1 },
            new() { Name = "issuer", DisplayName = "Issuer", FieldTypeName = TextFieldType.ControlName, DisplayOrder = 2 }
        }
    };

    [Fact]
    public async Task Import_Creates_Type_And_Fields_When_Absent_And_Stamps_Provenance()
    {
        var result = await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { SamplePack() }
        });

        result.TypesCreated.ShouldBe(1);
        result.FieldsCreated.ShouldBe(2);
        result.Items.Single().TypeAction.ShouldBe(PackItemAction.Created);

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.invoice");
            type.ShouldNotBeNull();
            type!.DisplayName.ShouldBe("Invoice");
            type.ConfidenceThreshold.ShouldBe(0.8);
            type.Priority.ShouldBe(5);
            // Provenance stamped in ExtraProperties (config metadata, not the Document truth source).
            type.GetProperty<string>(DocumentTypePackConsts.ProvenanceSourceKey)
                .ShouldBe(DocumentTypePackConsts.ProvenanceSourceValue);

            var fields = await _fieldDefinitionRepository.GetListAsync(type.Id);
            fields.Select(f => f.Name).OrderBy(n => n).ShouldBe(new[] { "amount", "issuer" });
            fields.Single(f => f.Name == "amount").FieldTypeName.ShouldBe(NumberFieldType.ControlName);
        });
    }

    /// <summary>
    /// #625: pack import is a second write path into <see cref="Field"/> rows, alongside
    /// <see cref="IFieldDefinitionAppService.CreateAsync"/>/<c>UpdateAsync</c>, and owes an imported Table
    /// field's columns the same recursive registry check - not just the kernel's generic shape validation.
    /// </summary>
    [Fact]
    public async Task Import_Rejects_A_Table_Column_With_An_Unregistered_FieldTypeName()
    {
        var pack = SamplePack();
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName },
                    new() { Name = "qty", DisplayName = "Quantity", FieldTypeName = "SomeFutureType" }
                }
            }.ConfigurationDictionary
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.UnknownColumnFieldType);
    }

    /// <summary>
    /// #625 follow-up: a Table column's <c>Name</c> is concatenated raw into the LLM's JSON schema message
    /// exactly like a top-level <c>Field.Name</c> is, so an imported pack owes it the same prompt-injection
    /// allow-list check as <see cref="IFieldDefinitionAppService.CreateAsync"/>/<c>UpdateAsync</c>.
    /// </summary>
    [Fact]
    public async Task Import_Rejects_A_Table_Column_With_An_Invalid_Name()
    {
        var pack = SamplePack();
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName },
                    new() { Name = "bad name!\n", DisplayName = "Bad", FieldTypeName = TextFieldType.ControlName }
                }
            }.ConfigurationDictionary
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.InvalidColumnName);
    }

    /// <summary>
    /// #625 follow-up regression test: before the recursive nesting-depth gate existed, only the IMMEDIATE
    /// columns' FieldTypeName were checked, so an unregistered type nested two levels deep (Table -&gt; Table
    /// column -&gt; bad grandchild column) passed this gate and would only fail later, uncaught, inside
    /// TableFieldTypeExtension.BuildExtractionSchema's own defensive NotSupportedException.
    /// </summary>
    [Fact]
    public async Task Import_Rejects_An_Unregistered_Column_Type_Nested_Two_Levels_Deep()
    {
        var pack = SamplePack();
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = NestedTableConfiguration(2, "SomeFutureType")
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.UnknownColumnFieldType);
    }

    /// <summary>A configuration nesting composite types exactly to CompositeFieldNesting.MaxDepth (Table &gt; Table &gt; Text, depth 3) imports normally.</summary>
    [Fact]
    public async Task Import_Accepts_A_Nested_Table_At_The_Max_Depth()
    {
        var pack = SamplePack();
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = NestedTableConfiguration(2)
        });

        var result = await _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } });

        result.FieldsCreated.ShouldBe(3);
    }

    /// <summary>One level past CompositeFieldNesting.MaxDepth (Table &gt; Table &gt; Table &gt; Text, depth 4) is refused before anything recurses into the configuration itself.</summary>
    [Fact]
    public async Task Import_Rejects_A_Nested_Table_Exceeding_The_Max_Depth()
    {
        var pack = SamplePack();
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = NestedTableConfiguration(3)
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.CompositeNestingTooDeep);
    }

    /// <summary>
    /// #625 follow-up (Option A: block, not migrate) — pack import is a second write path into <see cref="Field"/>
    /// rows, and owes a Table field's already-extracted columns the same guard
    /// <see cref="IFieldDefinitionAppService.UpdateAsync"/> enforces: its own FieldTypeName staying "Table"
    /// does not excuse a column rename once a document holds a value under the old shape.
    /// </summary>
    [Fact]
    public async Task Import_Blocks_A_Table_Column_Rename_When_The_Field_Has_Values()
    {
        var pack = SamplePack("host.table-guard");
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName }
                }
            }.ConfigurationDictionary
        });

        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } });
        await SeedDocumentWithValueAsync("host.table-guard", "line_items", "placeholder");

        var renamedPack = SamplePack("host.table-guard");
        renamedPack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item_name", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName }
                }
            }.ConfigurationDictionary
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { renamedPack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.DataTypeChangeNotAllowed);
    }

    /// <summary>The no-values counterpart: a genuinely fresh Table field's columns stay freely re-importable.</summary>
    [Fact]
    public async Task Import_Allows_A_Table_Column_Change_When_The_Field_Has_No_Values()
    {
        var pack = SamplePack("host.table-guard-free");
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName }
                }
            }.ConfigurationDictionary
        });

        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } });

        var renamedPack = SamplePack("host.table-guard-free");
        renamedPack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "line_items",
            DisplayName = "Line items",
            FieldTypeName = TableFieldType.ControlName,
            DisplayOrder = 3,
            IsSearchable = false,
            Configuration = new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "item_name", DisplayName = "Item", FieldTypeName = TextFieldType.ControlName }
                }
            }.ConfigurationDictionary
        });

        var result = await _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { renamedPack } });

        result.FieldsUpdated.ShouldBe(3); // amount, issuer, line_items
    }

    [Fact]
    public async Task Export_Round_Trips_An_Imported_Pack()
    {
        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { SamplePack() }
        });

        DocumentTypePackDto exported = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.invoice");
            exported = await _packAppService.ExportAsync(type!.Id);
        });

        exported.Version.ShouldBe(DocumentTypePackConsts.CurrentVersion);
        exported.TypeCode.ShouldBe("host.invoice");
        exported.DisplayName.ShouldBe("Invoice");
        exported.Description.ShouldBe("Invoice documents");
        exported.ConfidenceThreshold.ShouldBe(0.8);
        exported.Priority.ShouldBe(5);
        exported.Fields.Count.ShouldBe(2);
        // Export orders by DisplayOrder, so amount (1) precedes issuer (2).
        exported.Fields[0].Name.ShouldBe("amount");
        exported.Fields[0].Description.ShouldBe("the total");
        exported.Fields[0].FieldTypeName.ShouldBe(NumberFieldType.ControlName);
        exported.Fields[1].Name.ShouldBe("issuer");
    }

    [Fact]
    public async Task Reimport_Is_Idempotent_And_Produces_No_Duplicates()
    {
        var input = new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { SamplePack() } };

        await _packAppService.ImportAsync(input);
        var second = await _packAppService.ImportAsync(input);

        // Second run updates in place — no new rows.
        second.TypesCreated.ShouldBe(0);
        second.TypesUpdated.ShouldBe(1);
        second.FieldsCreated.ShouldBe(0);
        second.FieldsUpdated.ShouldBe(2);

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.invoice");
            type.ShouldNotBeNull();
            var fields = await _fieldDefinitionRepository.GetListAsync(type!.Id);
            fields.Count.ShouldBe(2); // no duplicate field rows
        });
    }

    [Fact]
    public async Task CreateOnly_Skips_Existing_Type_But_Adds_Missing_Fields()
    {
        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { SamplePack() }
        });

        // Same type code, changed displayName + changed existing-field prompt + one new field, in CreateOnly.
        var additivePack = SamplePack();
        additivePack.DisplayName = "CHANGED";
        additivePack.Fields.Single(f => f.Name == "amount").Description = "CHANGED";
        additivePack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "duedate",
            DisplayName = "Due date",
            FieldTypeName = DateTimeFieldType.ControlName,
            DisplayOrder = 3
        });

        var result = await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { additivePack },
            Mode = PackImportMode.CreateOnly
        });

        result.Items.Single().TypeAction.ShouldBe(PackItemAction.Skipped);
        result.FieldsCreated.ShouldBe(1);
        result.FieldsSkipped.ShouldBe(2);

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.invoice");
            type!.DisplayName.ShouldBe("Invoice"); // existing type left untouched

            var fields = await _fieldDefinitionRepository.GetListAsync(type.Id);
            fields.Count.ShouldBe(3); // the new field was added
            fields.Single(f => f.Name == "amount").Description.ShouldBe("the total"); // existing field untouched
        });
    }

    [Fact]
    public async Task Prompt_longer_than_4000_round_trips_through_export_and_reimport()
    {
        var type = await _documentTypeAppService.CreateAsync(new CreateDocumentTypeDto
        {
            TypeCode = "host.long-prompt",
            DisplayName = "Long prompt"
        });
        var prompt = new string('x', 5_000);
        await _fieldDefinitionAppService.CreateAsync(new CreateFieldDefinitionDto
        {
            DocumentTypeId = type.Id,
            Name = "body",
            DisplayName = "Body",
            Description = prompt,
            FieldTypeName = CKEditorFieldType.ControlName,
            // CKEditor has no index slot, so IsSearchable must be turned off explicitly — CreateFieldDefinitionDto's
            // default of true is right for the six indexable types and wrong for this one; the AppService's own
            // CheckSearchable now rejects the combination rather than silently accepting a switch that would do
            // nothing (#562).
            IsSearchable = false,
        });

        var exported = await _packAppService.ExportAsync(type.Id);
        exported.Fields.Single().Description.ShouldBe(prompt);

        var result = await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { exported }
        });

        result.FieldsUpdated.ShouldBe(1);
        var reExported = await _packAppService.ExportAsync(type.Id);
        reExported.Fields.Single().Description.ShouldBe(prompt);
    }

    /// <summary>
    /// #626's unique-key guard holds on the pack write path too: a new field of a type with no index slot
    /// (CKEditor) cannot arrive as a duplicate-detection key through an import when the field designer
    /// refuses it.
    /// </summary>
    [Fact]
    public async Task Import_rejects_a_new_non_indexable_field_marked_as_unique_key()
    {
        var pack = SamplePack("host.unique-ckeditor");
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "body",
            DisplayName = "Body",
            FieldTypeName = CKEditorFieldType.ControlName,
            IsSearchable = false,
            IsUniqueKey = true,
            DisplayOrder = 3
        });

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.FieldTypeNotUniqueKeyable);
        ex.Data["FieldTypeName"].ShouldBe(CKEditorFieldType.ControlName);
    }

    /// <summary>The CreateOrUpdate half: an existing non-indexable field cannot be turned into a unique key by re-import.</summary>
    [Fact]
    public async Task Import_rejects_turning_an_existing_non_indexable_field_into_a_unique_key()
    {
        var pack = SamplePack("host.unique-ckeditor-update");
        pack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "body",
            DisplayName = "Body",
            FieldTypeName = CKEditorFieldType.ControlName,
            IsSearchable = false,
            DisplayOrder = 3
        });
        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { pack }
        });

        pack.Fields.Single(f => f.Name == "body").IsUniqueKey = true;

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput
            {
                Packs = new List<DocumentTypePackDto> { pack },
                Mode = PackImportMode.CreateOrUpdate
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.FieldTypeNotUniqueKeyable);
        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode);
            var fields = await _fieldDefinitionRepository.GetListAsync(type!.Id);
            fields.Single(f => f.Name == "body").IsUniqueKey.ShouldBeFalse();
        });
    }

    [Fact]
    public async Task Import_rejects_a_whole_pack_whose_total_prompt_budget_is_exceeded_before_writing()
    {
        var firstLength = _behaviorOptions.MaxFieldSchemaPromptLength / 2;
        var pack = SamplePack("host.over-budget");
        pack.Fields = new List<DocumentTypePackFieldDto>
        {
            new()
            {
                Name = "first",
                DisplayName = "First",
                Description = new string('a', firstLength),
                FieldTypeName = TextFieldType.ControlName
            },
            new()
            {
                Name = "second",
                DisplayName = "Second",
                Description = new string('b', _behaviorOptions.MaxFieldSchemaPromptLength - firstLength + 1),
                FieldTypeName = TextFieldType.ControlName
            }
        };

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput { Packs = new List<DocumentTypePackDto> { pack } }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.FieldDefinition.SchemaPromptBudgetExceeded);
        ex.Data["ActualLength"].ShouldBe((long)_behaviorOptions.MaxFieldSchemaPromptLength + 1);
        await WithUnitOfWorkAsync(async () =>
            (await _documentTypeRepository.FindByTypeCodeAsync(pack.TypeCode)).ShouldBeNull());
    }

    [Fact]
    public async Task Unsupported_Version_Is_Rejected_Before_Any_Write()
    {
        var goodPack = SamplePack("host.alpha");
        var badPack = SamplePack("host.beta");
        badPack.Version = 999;

        var ex = await Should.ThrowAsync<BusinessException>(() => _packAppService.ImportAsync(
            new ImportDocumentTypePacksInput
            {
                Packs = new List<DocumentTypePackDto> { goodPack, badPack }
            }));

        ex.Code.ShouldBe(VaultExtractErrorCodes.DocumentTypePack.UnsupportedVersion);

        // Version is validated for the whole batch before any write, so the valid pack ahead of the bad one
        // is not partially applied.
        await WithUnitOfWorkAsync(async () =>
            (await _documentTypeRepository.FindByTypeCodeAsync("host.alpha")).ShouldBeNull());
    }

    /// <summary>
    /// A pack file exported before field architecture v3 (#559): <c>dataType</c> / <c>allowMultiple</c> /
    /// <c>prompt</c>, and no <c>fieldTypeName</c>. Version 1 is still inside
    /// [<c>MinSupportedVersion</c>, <c>CurrentVersion</c>], so import upconverts it in place rather than
    /// rejecting it. <c>IsSearchable</c> is deliberately left at the DTO default — exactly what a real v1
    /// JSON file, which has no such key, deserializes to.
    /// </summary>
    private static DocumentTypePackDto V1Pack(string typeCode, params DocumentTypePackFieldDto[] fields) => new()
    {
        Version = 1,
        TypeCode = typeCode,
        DisplayName = "Legacy",
        Fields = fields.ToList()
    };

    /// <summary>
    /// The v1-pack half of the regression fixed in <c>7c6650b9</c>: LongText's v3 target (CKEditor) has no
    /// index slot, so carrying v1's "every extracted value is indexed" forward as <c>IsSearchable = true</c>
    /// collided with the <c>CheckSearchable</c> guard and failed the whole import with no per-item recovery.
    /// </summary>
    [Fact]
    public async Task V1_pack_upconverts_LongText_to_a_non_indexed_CKEditor_field()
    {
        var result = await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto>
            {
                V1Pack("host.v1-longtext", new DocumentTypePackFieldDto
                {
                    Name = "summary",
                    DisplayName = "Summary",
                    Prompt = "the executive summary",
                    DataType = FieldDataType.LongText
                })
            }
        });

        result.TypesCreated.ShouldBe(1);
        result.FieldsCreated.ShouldBe(1);

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.v1-longtext");
            var field = (await _fieldDefinitionRepository.GetListAsync(type!.Id)).Single();

            field.FieldTypeName.ShouldBe(CKEditorFieldType.ControlName);
            field.IsSearchable.ShouldBeFalse();
            // v1's `prompt` is v2's `description`: the LLM briefing survives the rename rather than being dropped.
            field.Description.ShouldBe("the executive summary");

            // These values are plain text / Markdown pulled out of a document, never HTML, so the field type's
            // own Html default would be wrong for every upconverted field.
            var config = new CKEditorConfiguration(field.Configuration);
            config.ContentFormat.ShouldBe(CKEditorContentFormat.Markdown);
            config.Mode.ShouldBe(CKEditorMode.Basic);
        });
    }

    /// <summary>
    /// "One value or many" stopped being a flag beside the type and became a property of the type itself, so
    /// v1's multi-valued text field has to land on Tags — the open-vocabulary multi-value type, not Select,
    /// which validates against a configured option list a legacy field never had — and carry v2's own
    /// count / length ceilings forward as configuration.
    /// </summary>
    [Fact]
    public async Task V1_pack_upconverts_a_multi_valued_text_field_to_Tags()
    {
        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto>
            {
                V1Pack("host.v1-tags", new DocumentTypePackFieldDto
                {
                    Name = "parties",
                    DisplayName = "Parties",
                    DataType = FieldDataType.Text,
                    AllowMultiple = true
                })
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.v1-tags");
            var field = (await _fieldDefinitionRepository.GetListAsync(type!.Id)).Single();

            field.FieldTypeName.ShouldBe(TagsFieldType.ControlName);
            // Tags is indexable, so here v1's blanket indexing does carry forward untouched.
            field.IsSearchable.ShouldBeTrue();

            var config = new TagsConfiguration(field.Configuration);
            config.MaxCount.ShouldBe(DocumentExtractedFieldConsts.MaxMultiValueCount);
            config.MaxLength.ShouldBe(DocumentExtractedFieldConsts.MaxTextValueLength);
        });
    }

    /// <summary>
    /// The rest of the v1 vocabulary in one pack. The row that earns the test is Date versus DateTime: v3
    /// folds both into one field type, so the distinction survives only by moving into the configuration's
    /// <c>InputMode</c> — a mapping that would look correct on the field type alone while silently flattening
    /// every pure date into a datetime with invented hours.
    /// </summary>
    [Fact]
    public async Task V1_pack_maps_each_remaining_data_type_to_its_v3_field_type()
    {
        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto>
            {
                V1Pack(
                    "host.v1-scalars",
                    new DocumentTypePackFieldDto { Name = "issuer", DisplayName = "Issuer", DataType = FieldDataType.Text },
                    new DocumentTypePackFieldDto { Name = "amount", DisplayName = "Amount", DataType = FieldDataType.Number },
                    new DocumentTypePackFieldDto { Name = "paid", DisplayName = "Paid", DataType = FieldDataType.Boolean },
                    new DocumentTypePackFieldDto { Name = "issued-on", DisplayName = "Issued on", DataType = FieldDataType.Date },
                    new DocumentTypePackFieldDto { Name = "signed-at", DisplayName = "Signed at", DataType = FieldDataType.DateTime })
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.v1-scalars");
            var fields = (await _fieldDefinitionRepository.GetListAsync(type!.Id)).ToDictionary(f => f.Name);

            fields["issuer"].FieldTypeName.ShouldBe(TextFieldType.ControlName);
            fields["amount"].FieldTypeName.ShouldBe(NumberFieldType.ControlName);
            fields["paid"].FieldTypeName.ShouldBe(BooleanFieldType.ControlName);

            fields["issued-on"].FieldTypeName.ShouldBe(DateTimeFieldType.ControlName);
            fields["signed-at"].FieldTypeName.ShouldBe(DateTimeFieldType.ControlName);
            new DateTimeConfiguration(fields["issued-on"].Configuration).InputMode.ShouldBe(DateTimeInputMode.Date);
            new DateTimeConfiguration(fields["signed-at"].Configuration).InputMode.ShouldBe(DateTimeInputMode.DateTime);

            // Every one of these types has an index slot, so none of them trips CheckSearchable.
            fields.Values.ShouldAllBe(f => f.IsSearchable);
        });
    }

    /// <summary>
    /// The upconvert has to be durable, not merely accepted. Re-exporting a type imported from a v1 pack must
    /// emit version 2 with the legacy members gone, and re-importing that export must match the same fields
    /// rather than reading as a field-type change — the "an exported-then-reimported type would stop matching
    /// the type it came from" failure <c>DocumentTypePackV1Upconverter</c>'s own doc comment warns about.
    /// </summary>
    [Fact]
    public async Task V1_pack_re_exports_as_version_2_and_reimports_onto_the_same_fields()
    {
        await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto>
            {
                V1Pack(
                    "host.v1-roundtrip",
                    new DocumentTypePackFieldDto { Name = "summary", DisplayName = "Summary", Prompt = "the gist", DataType = FieldDataType.LongText },
                    new DocumentTypePackFieldDto { Name = "parties", DisplayName = "Parties", DataType = FieldDataType.Text, AllowMultiple = true })
            }
        });

        DocumentTypePackDto exported = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.v1-roundtrip");
            exported = await _packAppService.ExportAsync(type!.Id);
        });

        exported.Version.ShouldBe(DocumentTypePackConsts.CurrentVersion);
        // Export never populates the legacy members, whatever version the rows arrived as.
        exported.Fields.ShouldAllBe(f => f.DataType == null && f.Prompt == null && !f.AllowMultiple);
        exported.Fields.Single(f => f.Name == "summary").Description.ShouldBe("the gist");
        exported.Fields.Single(f => f.Name == "parties").FieldTypeName.ShouldBe(TagsFieldType.ControlName);

        var reimport = await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { exported }
        });

        reimport.FieldsCreated.ShouldBe(0);
        reimport.FieldsUpdated.ShouldBe(2);

        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync("host.v1-roundtrip");
            var fields = await _fieldDefinitionRepository.GetListAsync(type!.Id);
            fields.Count.ShouldBe(2); // matched by name, not duplicated under new field types
            fields.Single(f => f.Name == "summary").IsSearchable.ShouldBeFalse();
        });
    }

    /// <summary>
    /// A Table whose single column is a Table whose single column is... <paramref name="levels"/> deep,
    /// bottoming out in a column of <paramref name="leafFieldTypeName"/>. Mirrors
    /// <c>FieldTypeCatalogAndSearchability_Tests</c>'s own copy of this helper (each write path's test
    /// class keeps its own, the same duplication <c>EnsureFieldTypeRegistered</c> itself has across the two
    /// write paths).
    /// </summary>
    private static FieldConfigurationDictionary NestedTableConfiguration(int levels, string leafFieldTypeName = TextFieldType.ControlName)
    {
        if (levels <= 1)
        {
            return new TableConfiguration
            {
                Columns = new List<InlineFieldDefinition>
                {
                    new() { Name = "label", DisplayName = "Label", FieldTypeName = leafFieldTypeName }
                }
            }.ConfigurationDictionary;
        }

        return new TableConfiguration
        {
            Columns = new List<InlineFieldDefinition>
            {
                new()
                {
                    Name = "nested",
                    DisplayName = "Nested",
                    FieldTypeName = TableFieldType.ControlName,
                    Configuration = NestedTableConfiguration(levels - 1, leafFieldTypeName)
                }
            }
        }.ConfigurationDictionary;
    }

    /// <summary>Seeds one document holding <paramref name="value"/> under <paramref name="fieldName"/>, so <c>AnyFlexFieldValueAsync</c>'s "does any document hold this field" guard finds it.</summary>
    private async Task<Guid> SeedDocumentWithValueAsync(string typeCode, string fieldName, object value)
    {
        var documentRepository = GetRequiredService<IDocumentRepository>();
        var documentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var type = await _documentTypeRepository.FindByTypeCodeAsync(typeCode);
            var doc = new Document(documentId, tenantId: null, DocumentTestData.NewFileOrigin(documentId));
            DocumentTestData.MarkClassified(doc, type!.Id);
            doc.SetFlexFields(new Dictionary<string, object?> { [fieldName] = value });
            await documentRepository.InsertAsync(doc, autoSave: true);
        });
        return documentId;
    }
}
