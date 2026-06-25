namespace Dignite.Vault.Extract.Documents.DocumentTypes;

public static class DocumentTypeConsts
{
    public static int MaxTypeCodeLength { get; set; } = 128;
    public static int MaxDisplayNameLength { get; set; } = 128;

    /// <summary>
    /// Length limit for <see cref="DocumentType.Description"/>. Description is optional classification
    /// helper text and is fed only into the classification prompt to help the LLM classify types. One
    /// or two characteristic sentences are enough; excessive length dilutes the classification signal,
    /// so this limit is far below document body length.
    /// </summary>
    public static int MaxDescriptionLength { get; set; } = 512;

    /// <summary>
    /// Whitelist for <see cref="DocumentType.TypeCode"/>: only letter / digit / underscore / hyphen
    /// segments are allowed, separated by <c>.</c>. Single-segment and multi-segment values are both
    /// valid; leading, trailing, or consecutive <c>.</c> is not allowed. This serves the same prompt
    /// injection defense purpose as <see cref="FieldDefinitionConsts.NamePattern"/>: TypeCode is
    /// concatenated raw into the LLM system prompt in <c>DocumentClassificationWorkflow</c>, so the
    /// character set must exclude injection vectors such as newlines, quotes, and Markdown control
    /// characters.
    /// <para>
    /// Must be <c>const</c>: as a safety boundary in the LLM prompt injection defense chain, any
    /// runtime mutation would pierce the whitelist. <see cref="DocumentType"/> caches this field once
    /// as a <c>static readonly Regex</c> when the type loads, so runtime overrides would not take
    /// effect anyway.
    /// </para>
    /// <para>
    /// Length is independently constrained by <see cref="MaxTypeCodeLength"/> in
    /// <c>Check.NotNullOrWhiteSpace</c>.
    /// </para>
    /// </summary>
    public const string TypeCodePattern = @"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$";
}
