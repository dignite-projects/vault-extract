using Dignite.Vault.Extract.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace Dignite.Vault.Extract.Permissions;

public class VaultExtractPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(VaultExtractPermissions.GroupName, L("Permission:Extract"));

        var documents = group.AddPermission(VaultExtractPermissions.Documents.Default, L("Permission:Documents"));
        documents.AddChild(VaultExtractPermissions.Documents.Upload, L("Permission:Documents.Upload"));
        documents.AddChild(VaultExtractPermissions.Documents.Delete, L("Permission:Documents.Delete"));
        documents.AddChild(VaultExtractPermissions.Documents.PermanentDelete, L("Permission:Documents.PermanentDelete"));
        documents.AddChild(VaultExtractPermissions.Documents.Restore, L("Permission:Documents.Restore"));
        documents.AddChild(VaultExtractPermissions.Documents.Export, L("Permission:Documents.Export"));
        documents.AddChild(VaultExtractPermissions.Documents.ConfirmClassification, L("Permission:Documents.ConfirmClassification"));

        var pipelines = documents.AddChild(VaultExtractPermissions.Documents.Pipelines.Default, L("Permission:Documents.Pipelines"));
        pipelines.AddChild(VaultExtractPermissions.Documents.Pipelines.Retry, L("Permission:Documents.Pipelines.Retry"));

        var reprocessing = documents.AddChild(VaultExtractPermissions.Documents.Reprocessing.Default, L("Permission:Documents.Reprocessing"));
        reprocessing.AddChild(VaultExtractPermissions.Documents.Reprocessing.FieldExtraction, L("Permission:Documents.Reprocessing.FieldExtraction"));
        reprocessing.AddChild(VaultExtractPermissions.Documents.Reprocessing.Reclassification, L("Permission:Documents.Reprocessing.Reclassification"));

        var cabinets = group.AddPermission(VaultExtractPermissions.Cabinets.Default, L("Permission:Cabinets"));
        cabinets.AddChild(VaultExtractPermissions.Cabinets.Create, L("Permission:Cabinets.Create"));
        cabinets.AddChild(VaultExtractPermissions.Cabinets.Update, L("Permission:Cabinets.Update"));
        cabinets.AddChild(VaultExtractPermissions.Cabinets.Delete, L("Permission:Cabinets.Delete"));

        var documentTypes = group.AddPermission(VaultExtractPermissions.DocumentTypes.Default, L("Permission:DocumentTypes"));
        documentTypes.AddChild(VaultExtractPermissions.DocumentTypes.Create, L("Permission:DocumentTypes.Create"));
        documentTypes.AddChild(VaultExtractPermissions.DocumentTypes.Update, L("Permission:DocumentTypes.Update"));
        documentTypes.AddChild(VaultExtractPermissions.DocumentTypes.Delete, L("Permission:DocumentTypes.Delete"));

        var fieldDefinitions = group.AddPermission(VaultExtractPermissions.FieldDefinitions.Default, L("Permission:FieldDefinitions"));
        fieldDefinitions.AddChild(VaultExtractPermissions.FieldDefinitions.Create, L("Permission:FieldDefinitions.Create"));
        fieldDefinitions.AddChild(VaultExtractPermissions.FieldDefinitions.Update, L("Permission:FieldDefinitions.Update"));
        fieldDefinitions.AddChild(VaultExtractPermissions.FieldDefinitions.Delete, L("Permission:FieldDefinitions.Delete"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<VaultExtractResource>(name);
    }
}
