namespace Dignite.Vault.Extract.Documents.Cabinets;

public static class CabinetConsts
{
    public static int MaxNameLength { get; set; } = 128;

    /// <summary>
    /// Maximum length for <see cref="Cabinet.Description"/>. Description is optional helper text for
    /// cabinet selection (#273) and is fed only into the #265 cabinet-selection prompt. One or two
    /// sentences are enough; longer text dilutes the signal and adds tokens, so the limit is much lower
    /// than document body length.
    /// </summary>
    public static int MaxDescriptionLength { get; set; } = 512;
}
