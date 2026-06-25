namespace Dignite.Vault.Extract;

public static class ExtractDbProperties
{
    public static string DbTablePrefix { get; set; } = "Extract";

    public static string? DbSchema { get; set; } = null;

    public const string ConnectionStringName = "Extract";
}
