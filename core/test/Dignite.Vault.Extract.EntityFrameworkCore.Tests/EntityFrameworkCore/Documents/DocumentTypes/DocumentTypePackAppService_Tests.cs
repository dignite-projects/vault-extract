using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.DocumentTypes;
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
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly VaultExtractBehaviorOptions _behaviorOptions;

    public DocumentTypePackAppService_Tests()
    {
        _packAppService = GetRequiredService<IDocumentTypePackAppService>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
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
            new() { Name = "amount", DisplayName = "Amount", Prompt = "the total", DataType = FieldDataType.Number, DisplayOrder = 1 },
            new() { Name = "issuer", DisplayName = "Issuer", DataType = FieldDataType.Text, DisplayOrder = 2 }
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
            fields.Single(f => f.Name == "amount").DataType.ShouldBe(FieldDataType.Number);
        });
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
        exported.Fields[0].Prompt.ShouldBe("the total");
        exported.Fields[0].DataType.ShouldBe(FieldDataType.Number);
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
        additivePack.Fields.Single(f => f.Name == "amount").Prompt = "CHANGED";
        additivePack.Fields.Add(new DocumentTypePackFieldDto
        {
            Name = "duedate",
            DisplayName = "Due date",
            DataType = FieldDataType.Date,
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
            fields.Single(f => f.Name == "amount").Prompt.ShouldBe("the total"); // existing field untouched
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
            Prompt = prompt,
            DataType = FieldDataType.LongText
        });

        var exported = await _packAppService.ExportAsync(type.Id);
        exported.Fields.Single().Prompt.ShouldBe(prompt);

        var result = await _packAppService.ImportAsync(new ImportDocumentTypePacksInput
        {
            Packs = new List<DocumentTypePackDto> { exported }
        });

        result.FieldsUpdated.ShouldBe(1);
        var reExported = await _packAppService.ExportAsync(type.Id);
        reExported.Fields.Single().Prompt.ShouldBe(prompt);
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
                Prompt = new string('a', firstLength),
                DataType = FieldDataType.Text
            },
            new()
            {
                Name = "second",
                DisplayName = "Second",
                Prompt = new string('b', _behaviorOptions.MaxFieldSchemaPromptLength - firstLength + 1),
                DataType = FieldDataType.Text
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
}
