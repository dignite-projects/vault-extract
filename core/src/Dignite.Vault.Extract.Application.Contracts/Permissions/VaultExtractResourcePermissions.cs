namespace Dignite.Vault.Extract.Permissions;

/// <summary>
/// ABP resource-based authorization (#629, completed by #632): grants attached to one <c>DocumentType</c> row rather
/// than to the module as a whole. These are <b>not</b> standard permissions — they are never
/// checked by name alone, only as <c>AuthorizationService.IsGrantedAsync(documentType, name)</c>,
/// and they are stored in <c>AbpResourcePermissionGrants</c> keyed by the type's immutable Id.
/// <para>
/// <b>Both strings below are frozen wire contracts from the first grant row onwards</b>, the same
/// discipline CLAUDE.md applies to the <c>Extract:*</c> error codes: rename the holder class if you
/// must, never the persisted value. Changing either one silently orphans every existing grant.
/// </para>
/// <para>
/// #636: moved out of <see cref="VaultExtractPermissions.DocumentTypes"/> into its own holder. The family only
/// ever lived there because the names are related — it is a resource name plus four resource permissions, never
/// checked by name alone and never grantable from the ordinary permission-management grid, so it does not belong
/// inside the class that models the standard permission tree. <see cref="VaultExtractPermissions.GetAll"/> used to
/// exist solely to filter this family back out by string prefix; both it and the filter are gone now that the
/// family lives somewhere <c>GetAll</c> never reaches in the first place. String values are unchanged to the
/// character — only the holder moved.
/// </para>
/// </summary>
public static class VaultExtractResourcePermissions
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
    /// May upload a document declaring <b>this</b> document type, on its own — without
    /// <see cref="VaultExtractPermissions.Documents.Upload"/> (#645). Admits the caller to the #623 declared-type
    /// path (confidence 1.0, <c>Confirmed</c>, no classification LLM call) for one type only; the module-wide
    /// equivalent that admits every type of the layer, and an untyped upload, is
    /// <see cref="VaultExtractPermissions.Documents.Upload"/>. It is also the per-type arm of a reclassification's
    /// <b>target</b> type.
    /// </summary>
    public const string Upload = Name + ".Upload";

    /// <summary>
    /// May read the documents of <b>this</b> document type — detail, blob download, list / export rows,
    /// pipeline runs, and every MCP path that delegates to them (#632). The module-wide equivalent that
    /// admits every type of the layer is <see cref="VaultExtractPermissions.Documents.ReadAll"/>.
    /// </summary>
    public const string Read = Name + ".Read";

    /// <summary>
    /// May run the operator edit family on documents of <b>this</b> document type: confirm / reclassify /
    /// re-recognize / re-extract fields / update fields / correct Markdown / reject review / allow
    /// duplicate / resolve field validation warnings (#632). Module-wide equivalent:
    /// <see cref="VaultExtractPermissions.Documents.ConfirmClassification"/>. Reclassifying to another type
    /// additionally needs <see cref="Upload"/> on the <b>target</b> type, or one of the two module-wide permissions
    /// that admit every target type: <c>ConfirmClassification</c> or <c>Documents.Upload</c> (#645).
    /// </summary>
    public const string Edit = Name + ".Edit";

    /// <summary>
    /// May soft-delete documents of <b>this</b> document type, and restore them from the recycle bin (#632).
    /// Module-wide equivalent: <see cref="VaultExtractPermissions.Documents.Delete"/>, which likewise covers both —
    /// whoever may delete may undo (<c>DocumentAccessRule.Restore</c>; #645 merged the role level too). Permanent
    /// delete stays module-wide only, by decision.
    /// </summary>
    public const string Delete = Name + ".Delete";
}
