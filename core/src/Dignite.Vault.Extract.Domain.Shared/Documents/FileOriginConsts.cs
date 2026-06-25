namespace Dignite.Vault.Extract.Documents;

public static class FileOriginConsts
{
    public static int MaxBlobNameLength { get; set; } = 512;

    public static int MaxUploadedByUserNameLength { get; set; } = 256;

    public static int MaxOriginalFileNameLength { get; set; } = 512;

    public static int MaxContentTypeLength { get; set; } = 256;

    public static int MaxContentHashLength { get; set; } = 64;
}
