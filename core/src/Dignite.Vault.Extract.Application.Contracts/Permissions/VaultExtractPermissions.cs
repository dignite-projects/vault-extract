namespace Dignite.Vault.Extract.Permissions;

public class VaultExtractPermissions
{
    public const string GroupName = "VaultExtract";

    public static class Documents
    {
        /// <summary>
        /// <b>Entry</b>, not "read everything" (#632 decision 1). Holding this means only "may enter the documents
        /// area and work inside the caller's own type scope"; it is also the group parent every child below
        /// implies, and the SPA route gate. The module-wide "read every type of the layer" half is
        /// <see cref="ReadAll"/>.
        /// <para>
        /// The split exists because a per-type Read grant is unexpressible without it: this name was the gate on
        /// every read endpoint and on every SPA documents route, so every principal that can open the documents
        /// area holds it and therefore sees every document of the layer — a per-type Read would never narrow
        /// anyone. (ABP's <c>PermissionDefinition.Parent</c> is enforced by the permission-management dialog, which
        /// checks the parent with the child; <c>PermissionChecker</c> never consults it, so a permission granted
        /// programmatically through <c>IPermissionManager</c> can exist without its parent. The split rests on the
        /// route guard and the read gates, not on the parent rule.)
        /// </para>
        /// </summary>
        public const string Default = GroupName + ".Documents";

        /// <summary>
        /// The module-wide read (#632): every document of the caller's layer, whatever its type. It is to Read
        /// what <see cref="ConfirmClassification"/> is to Edit and <see cref="Delete"/> is to Delete — the
        /// module-wide half of the "module-wide permission OR the matching per-type grant" rule. Without it a
        /// caller sees only the types it holds a <see cref="VaultExtractResourcePermissions.Read"/> grant on, and
        /// never sees untyped documents at all.
        /// </summary>
        public const string ReadAll = Default + ".ReadAll";

        public const string Upload = Default + ".Upload";

        /// <summary>
        /// Soft-delete documents of <b>every</b> type of the caller's layer, and restore them from the recycle bin
        /// (#645 merged the former separate restore permission into this name: whoever may delete may undo). The
        /// per-type counterpart is <see cref="VaultExtractResourcePermissions.Delete"/>.
        /// </summary>
        public const string Delete = Default + ".Delete";

        public const string PermanentDelete = Default + ".PermanentDelete";
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

        /// <summary>
        /// May open ABP's resource-permission dialog for a <c>DocumentType</c> and grant / revoke the
        /// per-type grants in <see cref="VaultExtractResourcePermissions"/> (#629). Deliberately separate from
        /// <see cref="Update"/>: handing out access is a different responsibility from editing the schema, and
        /// this name is the only gate ABP puts on the <c>/api/permission-management/permissions/resource*</c>
        /// endpoints.
        /// </summary>
        public const string ManagePermissions = Default + ".ManagePermissions";

        // The per-document-type resource-permission family (#629/#632) moved out to its own holder,
        // VaultExtractResourcePermissions (#636): it is a resource name plus four resource permissions, never
        // checked by name alone, and does not belong inside the class that models the standard permission tree.
        // String values are unchanged to the character.
    }

    // Field definition schema management (#217): admin-level operations independent of document CRUD.
    public static class FieldDefinitions
    {
        public const string Default = GroupName + ".FieldDefinitions";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }
}
