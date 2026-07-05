namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>Hard limits for the bounded cabinet discovery read use case.</summary>
public static class CabinetReadConsts
{
    /// <summary>
    /// Maximum cabinets materialized by one discovery query. This is a compile-time safety boundary,
    /// not a paging window; callers narrow by cabinet name when the result is truncated.
    /// </summary>
    public const int MaxResultCount = 100;
}
