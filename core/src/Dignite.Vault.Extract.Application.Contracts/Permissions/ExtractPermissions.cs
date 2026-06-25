using Volo.Abp.Reflection;

namespace Dignite.Vault.Extract.Permissions;

public class ExtractPermissions
{
    public const string GroupName = "VaultExtract";

    public static class Documents
    {
        public const string Default = GroupName + ".Documents";
        public const string Upload = Default + ".Upload";
        public const string Delete = Default + ".Delete";
        public const string PermanentDelete = Default + ".PermanentDelete";
        public const string Restore = Default + ".Restore";
        public const string Export = Default + ".Export";
        public const string ConfirmClassification = Default + ".ConfirmClassification";

        public static class Pipelines
        {
            public const string Default = Documents.Default + ".Pipelines";
            public const string Retry = Default + ".Retry";
        }

        // Bulk reprocessing of existing documents (#289): admin-level operation used to rerun
        // existing documents after configuration changes such as classification prompts / field
        // definitions. Single-document "field re-extraction only" uses ConfirmClassification
        // (operator-level, symmetric with "re-recognize"); bulk entry points use this permission set.
        public static class Reprocessing
        {
            public const string Default = Documents.Default + ".Reprocessing";

            /// <summary>Bulk field re-extraction, a leaf operation with light warning.</summary>
            public const string FieldExtraction = Default + ".FieldExtraction";

            /// <summary>Bulk reclassification, cascading + destructive, with heavy warning.</summary>
            public const string Reclassification = Default + ".Reclassification";
        }

        public static class Templates
        {
            public const string Default = Documents.Default + ".Templates";
            public const string Create = Default + ".Create";
            public const string Update = Default + ".Update";
            public const string Delete = Default + ".Delete";
        }
    }

    // Cabinets (#194): human organization dimension, sibling permission group to Documents.
    public static class Cabinets
    {
        public const string Default = GroupName + ".Cabinets";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // Document type schema management (#217): admin-level operations independent of document CRUD.
    public static class DocumentTypes
    {
        public const string Default = GroupName + ".DocumentTypes";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // Field definition schema management (#217): admin-level operations independent of document CRUD.
    public static class FieldDefinitions
    {
        public const string Default = GroupName + ".FieldDefinitions";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    public static string[] GetAll()
    {
        return ReflectionHelper.GetPublicConstantsRecursively(typeof(ExtractPermissions));
    }
}
