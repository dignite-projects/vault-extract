using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Exports;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Integration tests for the application-layer, layer-scoped uniqueness enforcement that replaced the
/// soft-delete-filtered DB unique indexes (#304). Run against a real database with ABP's
/// <c>IMultiTenant</c> / <c>ISoftDelete</c> global filters, exercising the four app services end-to-end
/// (the domain managers are the actual guarantors). They assert the three invariants the issue requires:
/// <list type="bullet">
/// <item>the same key is allowed across layers (Host <c>TenantId = null</c> vs a tenant GUID);</item>
/// <item>a duplicate within a single layer is rejected with the entity's namespaced error code;</item>
/// <item>the <c>delete -&gt; recreate -&gt; restore</c> semantics are preserved per entity (soft-delete-aware
/// for DocumentType / FieldDefinition; active-only for the recycle-bin-less Cabinet / ExportTemplate).</item>
/// </list>
/// </summary>
public class LayeredUniqueness_Tests : ExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly ICabinetAppService _cabinetAppService;
    private readonly IExportTemplateAppService _exportTemplateAppService;
    private readonly ICurrentTenant _currentTenant;

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public LayeredUniqueness_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        _cabinetAppService = GetRequiredService<ICabinetAppService>();
        _exportTemplateAppService = GetRequiredService<IExportTemplateAppService>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    // ─── DocumentType (soft-delete-aware, has restore) ─────────────────────────

    [Fact]
    public async Task DocumentType_Same_Code_Is_Allowed_Across_Layers()
    {
        // Host (TenantId = null) and a tenant may both define "contract": two legitimate rows distinguished by TenantId.
        await WithUnitOfWorkAsync(() => _documentTypeAppService.CreateAsync(NewType("contract")));

        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                var dto = await _documentTypeAppService.CreateAsync(NewType("contract"));
                dto.TypeCode.ShouldBe("contract");
            }
        });

        // Both rows persist and coexist, each visible only in its own layer (no leakage, exactly one per layer).
        await WithUnitOfWorkAsync(async () =>
        {
            var hostTypes = await _documentTypeAppService.GetVisibleAsync();
            hostTypes.Count(t => t.TypeCode == "contract").ShouldBe(1);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                var tenantTypes = await _documentTypeAppService.GetVisibleAsync();
                tenantTypes.Count(t => t.TypeCode == "contract").ShouldBe(1);
            }
        });
    }

    [Fact]
    public async Task DocumentType_Duplicate_Code_In_Same_Layer_Is_Rejected()
    {
        await WithUnitOfWorkAsync(() => _documentTypeAppService.CreateAsync(NewType("contract")));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _documentTypeAppService.CreateAsync(NewType("contract"))));
        ex.Code.ShouldBe(ExtractErrorCodes.DocumentType.CodeAlreadyExists);
    }

    [Fact]
    public async Task DocumentType_Delete_Recreate_Restore_Preserves_SoftDelete_Aware_Semantics()
    {
        var id = await WithUnitOfWorkAsync(async () =>
            (await _documentTypeAppService.CreateAsync(NewType("contract"))).Id);
        await WithUnitOfWorkAsync(() => _documentTypeAppService.DeleteAsync(id));

        // Recreate while a soft-deleted row still holds the code -> rejected (soft-delete-aware), so restoring the
        // original cannot produce two active rows with the same (TenantId, TypeCode).
        var ex = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _documentTypeAppService.CreateAsync(NewType("contract"))));
        ex.Code.ShouldBe(ExtractErrorCodes.DocumentType.CodeAlreadyExists);

        // Restoring the soft-deleted row succeeds because no active row holds the code.
        var restored = await WithUnitOfWorkAsync(() => _documentTypeAppService.RestoreAsync(id));
        restored.TypeCode.ShouldBe("contract");
    }

    // ─── FieldDefinition (soft-delete-aware, scoped to (TenantId, DocumentTypeId, Name)) ───

    [Fact]
    public async Task FieldDefinition_Duplicate_Name_Under_Same_Type_Is_Rejected()
    {
        var typeId = await WithUnitOfWorkAsync(async () =>
            (await _documentTypeAppService.CreateAsync(NewType("contract"))).Id);
        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.CreateAsync(NewField(typeId, "amount")));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _fieldDefinitionAppService.CreateAsync(NewField(typeId, "amount"))));
        ex.Code.ShouldBe(ExtractErrorCodes.FieldDefinition.AlreadyExists);
    }

    [Fact]
    public async Task FieldDefinition_Same_Name_Under_Different_Types_Is_Allowed()
    {
        var (typeA, typeB) = await WithUnitOfWorkAsync(async () =>
        {
            var a = await _documentTypeAppService.CreateAsync(NewType("contract"));
            var b = await _documentTypeAppService.CreateAsync(NewType("invoice"));
            return (a.Id, b.Id);
        });

        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.CreateAsync(NewField(typeA, "amount")));
        await WithUnitOfWorkAsync(async () =>
        {
            var dto = await _fieldDefinitionAppService.CreateAsync(NewField(typeB, "amount"));
            dto.Name.ShouldBe("amount");
        });
    }

    [Fact]
    public async Task FieldDefinition_Delete_Recreate_Restore_Preserves_SoftDelete_Aware_Semantics()
    {
        var (typeId, fieldId) = await WithUnitOfWorkAsync(async () =>
        {
            var t = await _documentTypeAppService.CreateAsync(NewType("contract"));
            var f = await _fieldDefinitionAppService.CreateAsync(NewField(t.Id, "amount"));
            return (t.Id, f.Id);
        });
        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.DeleteAsync(fieldId));

        // Recreate the same name while a soft-deleted field holds it -> rejected (soft-delete-aware).
        var ex = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _fieldDefinitionAppService.CreateAsync(NewField(typeId, "amount"))));
        ex.Code.ShouldBe(ExtractErrorCodes.FieldDefinition.AlreadyExists);

        var restored = await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.RestoreAsync(fieldId));
        restored.Name.ShouldBe("amount");
    }

    // ─── Cabinet (active-only, no recycle bin) ─────────────────────────────────

    [Fact]
    public async Task Cabinet_Duplicate_Name_In_Same_Layer_Is_Rejected()
    {
        await WithUnitOfWorkAsync(() => _cabinetAppService.CreateAsync(new CreateCabinetDto { Name = "Legal" }));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _cabinetAppService.CreateAsync(new CreateCabinetDto { Name = "Legal" })));
        ex.Code.ShouldBe(ExtractErrorCodes.Cabinet.NameAlreadyExists);
    }

    [Fact]
    public async Task Cabinet_Same_Name_Is_Allowed_Across_Layers()
    {
        await WithUnitOfWorkAsync(() => _cabinetAppService.CreateAsync(new CreateCabinetDto { Name = "Legal" }));

        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                var dto = await _cabinetAppService.CreateAsync(new CreateCabinetDto { Name = "Legal" });
                dto.Name.ShouldBe("Legal");
            }
        });

        // Both rows persist and coexist, each visible only in its own layer (no leakage, exactly one per layer).
        await WithUnitOfWorkAsync(async () =>
        {
            var hostCabinets = await _cabinetAppService.GetListAsync();
            hostCabinets.Count(c => c.Name == "Legal").ShouldBe(1);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                var tenantCabinets = await _cabinetAppService.GetListAsync();
                tenantCabinets.Count(c => c.Name == "Legal").ShouldBe(1);
            }
        });
    }

    [Fact]
    public async Task Cabinet_SoftDeleted_Name_Can_Be_Reused()
    {
        // Cabinet has no recycle bin / restore path, so the check is intentionally active-only: a soft-deleted
        // name is free for reuse. This preserves the behavior of the dropped IsDeleted = 0 filtered index.
        var id = await WithUnitOfWorkAsync(async () =>
            (await _cabinetAppService.CreateAsync(new CreateCabinetDto { Name = "Legal" })).Id);
        await WithUnitOfWorkAsync(() => _cabinetAppService.DeleteAsync(id));

        await WithUnitOfWorkAsync(async () =>
        {
            var dto = await _cabinetAppService.CreateAsync(new CreateCabinetDto { Name = "Legal" });
            dto.Name.ShouldBe("Legal");
        });
    }

    // ─── ExportTemplate (active-only, no restore) ──────────────────────────────

    [Fact]
    public async Task ExportTemplate_Duplicate_Name_In_Same_Layer_Is_Rejected()
    {
        var (typeId, fieldId) = await WithUnitOfWorkAsync(async () =>
        {
            var t = await _documentTypeAppService.CreateAsync(NewType("contract"));
            var f = await _fieldDefinitionAppService.CreateAsync(NewField(t.Id, "amount"));
            return (t.Id, f.Id);
        });

        await WithUnitOfWorkAsync(() => _exportTemplateAppService.CreateAsync(NewTemplate("Monthly", typeId, fieldId)));

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _exportTemplateAppService.CreateAsync(NewTemplate("Monthly", typeId, fieldId))));
        ex.Code.ShouldBe(ExtractErrorCodes.Export.TemplateNameAlreadyExists);
    }

    [Fact]
    public async Task ExportTemplate_Same_Name_Is_Allowed_Across_Layers()
    {
        // Host template.
        await WithUnitOfWorkAsync(async () =>
        {
            var t = await _documentTypeAppService.CreateAsync(NewType("contract"));
            var f = await _fieldDefinitionAppService.CreateAsync(NewField(t.Id, "amount"));
            await _exportTemplateAppService.CreateAsync(NewTemplate("Monthly", t.Id, f.Id));
        });

        // Same name in a tenant layer is a separate, legitimate row.
        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                var t = await _documentTypeAppService.CreateAsync(NewType("contract"));
                var f = await _fieldDefinitionAppService.CreateAsync(NewField(t.Id, "amount"));
                var dto = await _exportTemplateAppService.CreateAsync(NewTemplate("Monthly", t.Id, f.Id));
                dto.Name.ShouldBe("Monthly");
            }
        });

        // Both templates persist and coexist, each visible only in its own layer (no leakage, exactly one per layer).
        await WithUnitOfWorkAsync(async () =>
        {
            var hostTemplates = await _exportTemplateAppService.GetListAsync();
            hostTemplates.Count(t => t.Name == "Monthly").ShouldBe(1);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                var tenantTemplates = await _exportTemplateAppService.GetListAsync();
                tenantTemplates.Count(t => t.Name == "Monthly").ShouldBe(1);
            }
        });
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static CreateDocumentTypeDto NewType(string code) => new()
    {
        TypeCode = code,
        DisplayName = code,
        ConfidenceThreshold = 0.7,
        Priority = 0
    };

    private static CreateFieldDefinitionDto NewField(Guid documentTypeId, string name) => new()
    {
        DocumentTypeId = documentTypeId,
        Name = name,
        DisplayName = name,
        DataType = FieldDataType.Text
    };

    private static CreateExportTemplateDto NewTemplate(string name, Guid documentTypeId, Guid fieldDefinitionId) => new()
    {
        Name = name,
        Format = ExportFormat.Csv,
        DocumentTypeId = documentTypeId,
        Columns = new List<ExportColumnInput> { new() { FieldDefinitionId = fieldDefinitionId, Order = 0 } }
    };
}
