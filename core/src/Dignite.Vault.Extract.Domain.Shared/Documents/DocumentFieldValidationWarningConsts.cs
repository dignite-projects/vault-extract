namespace Dignite.Vault.Extract.Documents;

public static class DocumentFieldValidationWarningConsts
{
    /// <summary>
    /// Maximum length of one field validation warning <c>Message</c> (#527 §3).
    /// <para>
    /// This is both the DB column length (<c>Message nvarchar(512)</c>) and the domain invariant
    /// (<see cref="DocumentFieldValidationWarning.SetMessage"/>). They must stay aligned: the App layer safely
    /// truncates the untrusted model output at a valid UTF-16 character boundary to this ceiling before constructing a
    /// row, so every accepted message fits in storage. The warning message is a concise, human-readable explanation of
    /// a single field's validation mismatch, not a payload — 512 characters is generous. Changing this value requires
    /// regenerating an EF migration.
    /// </para>
    /// </summary>
    public static int MaxMessageLength { get; set; } = 512;

    /// <summary>
    /// Maximum number of field validation warnings accepted from one field-extraction LLM response (#527 §3).
    /// <para>
    /// A hard cap on untrusted model output: it bounds the JSON-schema <c>validationWarnings.maxItems</c> the LLM sees
    /// AND the server-side normalization that takes at most this many after discarding undeclared / blank / malformed
    /// entries and deduplicating by field. Since the model produces at most one merged warning per field, this also
    /// bounds the persisted <c>DocumentFieldValidationWarning</c> rows per document. Not host-tunable — it is a safety
    /// boundary on how much warning volume one extraction can create, so it is a compile-time constant, not a prompt
    /// ceiling.
    /// </para>
    /// </summary>
    public static int MaxWarningsPerExtraction { get; set; } = 32;
}
