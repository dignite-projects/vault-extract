using Dignite.Vault.Extract.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace Dignite.Vault.Extract.Permissions;

public class ExtractPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(ExtractPermissions.GroupName, L("Permission:Extract"));

        var documents = group.AddPermission(ExtractPermissions.Documents.Default, L("Permission:Documents"));
        documents.AddChild(ExtractPermissions.Documents.Upload, L("Permission:Documents.Upload"));
        documents.AddChild(ExtractPermissions.Documents.Delete, L("Permission:Documents.Delete"));
        documents.AddChild(ExtractPermissions.Documents.PermanentDelete, L("Permission:Documents.PermanentDelete"));
        documents.AddChild(ExtractPermissions.Documents.Restore, L("Permission:Documents.Restore"));
        documents.AddChild(ExtractPermissions.Documents.Export, L("Permission:Documents.Export"));
        documents.AddChild(ExtractPermissions.Documents.ConfirmClassification, L("Permission:Documents.ConfirmClassification"));

        var pipelines = documents.AddChild(ExtractPermissions.Documents.Pipelines.Default, L("Permission:Documents.Pipelines"));
        pipelines.AddChild(ExtractPermissions.Documents.Pipelines.Retry, L("Permission:Documents.Pipelines.Retry"));

        var reprocessing = documents.AddChild(ExtractPermissions.Documents.Reprocessing.Default, L("Permission:Documents.Reprocessing"));
        reprocessing.AddChild(ExtractPermissions.Documents.Reprocessing.FieldExtraction, L("Permission:Documents.Reprocessing.FieldExtraction"));
        reprocessing.AddChild(ExtractPermissions.Documents.Reprocessing.Reclassification, L("Permission:Documents.Reprocessing.Reclassification"));

        var templates = documents.AddChild(ExtractPermissions.Documents.Templates.Default, L("Permission:Documents.Templates"));
        templates.AddChild(ExtractPermissions.Documents.Templates.Create, L("Permission:Documents.Templates.Create"));
        templates.AddChild(ExtractPermissions.Documents.Templates.Update, L("Permission:Documents.Templates.Update"));
        templates.AddChild(ExtractPermissions.Documents.Templates.Delete, L("Permission:Documents.Templates.Delete"));

        var cabinets = group.AddPermission(ExtractPermissions.Cabinets.Default, L("Permission:Cabinets"));
        cabinets.AddChild(ExtractPermissions.Cabinets.Create, L("Permission:Cabinets.Create"));
        cabinets.AddChild(ExtractPermissions.Cabinets.Update, L("Permission:Cabinets.Update"));
        cabinets.AddChild(ExtractPermissions.Cabinets.Delete, L("Permission:Cabinets.Delete"));

        var documentTypes = group.AddPermission(ExtractPermissions.DocumentTypes.Default, L("Permission:DocumentTypes"));
        documentTypes.AddChild(ExtractPermissions.DocumentTypes.Create, L("Permission:DocumentTypes.Create"));
        documentTypes.AddChild(ExtractPermissions.DocumentTypes.Update, L("Permission:DocumentTypes.Update"));
        documentTypes.AddChild(ExtractPermissions.DocumentTypes.Delete, L("Permission:DocumentTypes.Delete"));

        var fieldDefinitions = group.AddPermission(ExtractPermissions.FieldDefinitions.Default, L("Permission:FieldDefinitions"));
        fieldDefinitions.AddChild(ExtractPermissions.FieldDefinitions.Create, L("Permission:FieldDefinitions.Create"));
        fieldDefinitions.AddChild(ExtractPermissions.FieldDefinitions.Update, L("Permission:FieldDefinitions.Update"));
        fieldDefinitions.AddChild(ExtractPermissions.FieldDefinitions.Delete, L("Permission:FieldDefinitions.Delete"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<ExtractResource>(name);
    }
}
