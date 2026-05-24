using Dignite.Paperbase.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace Dignite.Paperbase.Permissions;

public class PaperbasePermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(PaperbasePermissions.GroupName, L("Permission:Paperbase"));

        var documents = group.AddPermission(PaperbasePermissions.Documents.Default, L("Permission:Documents"));
        documents.AddChild(PaperbasePermissions.Documents.Upload, L("Permission:Documents.Upload"));
        documents.AddChild(PaperbasePermissions.Documents.Delete, L("Permission:Documents.Delete"));
        documents.AddChild(PaperbasePermissions.Documents.PermanentDelete, L("Permission:Documents.PermanentDelete"));
        documents.AddChild(PaperbasePermissions.Documents.Restore, L("Permission:Documents.Restore"));
        documents.AddChild(PaperbasePermissions.Documents.Export, L("Permission:Documents.Export"));
        documents.AddChild(PaperbasePermissions.Documents.ConfirmClassification, L("Permission:Documents.ConfirmClassification"));

        var pipelines = documents.AddChild(PaperbasePermissions.Documents.Pipelines.Default, L("Permission:Documents.Pipelines"));
        pipelines.AddChild(PaperbasePermissions.Documents.Pipelines.Retry, L("Permission:Documents.Pipelines.Retry"));

        var cabinets = group.AddPermission(PaperbasePermissions.Cabinets.Default, L("Permission:Cabinets"));
        cabinets.AddChild(PaperbasePermissions.Cabinets.Create, L("Permission:Cabinets.Create"));
        cabinets.AddChild(PaperbasePermissions.Cabinets.Update, L("Permission:Cabinets.Update"));
        cabinets.AddChild(PaperbasePermissions.Cabinets.Delete, L("Permission:Cabinets.Delete"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<PaperbaseResource>(name);
    }
}
