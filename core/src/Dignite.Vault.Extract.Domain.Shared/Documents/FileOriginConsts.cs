namespace Dignite.Vault.Extract.Documents;

public static class FileOriginConsts
{
    public static int MaxBlobNameLength { get; set; } = 512;

    public static int MaxUploadedByUserNameLength { get; set; } = 256;

    public static int MaxOriginalFileNameLength { get; set; } = 512;

    public static int MaxContentTypeLength { get; set; } = 256;

    public static int MaxContentHashLength { get; set; } = 64;

    /// <summary>
    /// What <c>FileOrigin.UploadedByUserName</c> becomes once the uploader's data has been erased (#698). A fixed,
    /// culture-neutral constant: it is persisted and returned on the wire as-is, so it is not localized here.
    /// <para>
    /// It must stay non-blank and within <see cref="MaxUploadedByUserNameLength"/>: the column has that length on SQL
    /// Server (SQLite, which the tests run on, does not enforce it), and <c>FileOrigin</c>'s own constructor rejects a
    /// blank or over-long name, so a placeholder that failed either would make the value unusable wherever one is
    /// rebuilt from it. <c>GdprUploaderPlaceholder_Tests</c> pins both.
    /// </para>
    /// </summary>
    public const string AnonymizedUploaderName = "[deleted user]";
}
