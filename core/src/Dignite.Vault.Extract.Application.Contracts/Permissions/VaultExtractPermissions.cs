using System;
using System.Linq;
using Volo.Abp.Reflection;

namespace Dignite.Vault.Extract.Permissions;

public class VaultExtractPermissions
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
        /// per-type grants below (#629). Deliberately separate from <see cref="Update"/>: handing out
        /// access is a different responsibility from editing the schema, and this name is the only gate
        /// ABP puts on the <c>/api/permission-management/permissions/resource*</c> endpoints.
        /// </summary>
        public const string ManagePermissions = Default + ".ManagePermissions";

        /// <summary>
        /// ABP resource-based authorization (#629): grants attached to one <c>DocumentType</c> row rather
        /// than to the module as a whole. These are <b>not</b> standard permissions — they are never
        /// checked by name alone, only as <c>AuthorizationService.IsGrantedAsync(documentType, name)</c>,
        /// and they are stored in <c>AbpResourcePermissionGrants</c> keyed by the type's immutable Id.
        /// <para>
        /// <b>Both strings below are frozen wire contracts from the first grant row onwards</b>, the same
        /// discipline CLAUDE.md applies to the <c>Extract:*</c> error codes: rename the holder class if you
        /// must, never the persisted value. Changing either one silently orphans every existing grant.
        /// </para>
        /// </summary>
        public static class Resources
        {
            /// <summary>
            /// MUST equal <c>typeof(DocumentType).FullName</c>: ABP's
            /// <c>KeyedObjectResourcePermissionRequirementHandler</c> derives the resource name from the
            /// runtime type of the object handed to <c>AuthorizationService</c>. Application.Contracts
            /// cannot reference Domain, so this is a literal, guarded by
            /// <c>DocumentTypeResourcePermissions_Tests</c>, which reds if the entity is ever renamed or
            /// moved.
            /// </summary>
            public const string Name = "Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType";

            /// <summary>
            /// May upload a document declaring <b>this</b> document type. Admits the caller to the #623
            /// declared-type path (confidence 1.0, <c>Confirmed</c>, no classification LLM call) for one
            /// type only; <c>Documents.ConfirmClassification</c> remains the module-wide equivalent that
            /// admits every type of the layer.
            /// </summary>
            public const string Upload = Name + ".Upload";
        }
    }

    // Field definition schema management (#217): admin-level operations independent of document CRUD.
    public static class FieldDefinitions
    {
        public const string Default = GroupName + ".FieldDefinitions";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    /// <summary>
    /// Every <b>standard</b> permission name defined here.
    /// <para>
    /// The <c>DocumentTypes.Resources</c> family (#629) is excluded on purpose:
    /// <see cref="DocumentTypes.Resources.Name"/> is a resource <i>name</i>, not a permission at all, and the
    /// resource permissions under it are only ever checked against a concrete <c>DocumentType</c> instance —
    /// feeding either one to something that treats this array as "the permissions to grant / seed / render"
    /// would produce a permission that can never be granted. Every resource permission is prefixed with the
    /// resource name, so the filter covers the ones phase 2 adds too.
    /// </para>
    /// </summary>
    public static string[] GetAll()
    {
        return ReflectionHelper.GetPublicConstantsRecursively(typeof(VaultExtractPermissions))
            .Where(name => !name.StartsWith(DocumentTypes.Resources.Name, StringComparison.Ordinal))
            .ToArray();
    }
}
