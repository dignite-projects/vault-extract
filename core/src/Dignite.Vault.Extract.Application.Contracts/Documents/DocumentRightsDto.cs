namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// What the calling principal may do with <b>this one document</b> (#635 decision 5), decided on the server and
/// sent down with the row.
/// <para>
/// Before #635 the server sent the client the <i>inputs</i> of the judgment — the caller's per-type grants plus
/// its module-wide permissions — and the client re-derived the rule from them. That copy keyed types by
/// <c>TypeCode</c> against the active types while the server keys by <c>Id</c> across soft-deleted ones, so a
/// document on an archived type came back with every action missing; and it had no way at all to express
/// ownership, which is not a property of a type. The judgment has one implementation now, and it is the one the
/// endpoint enforces.
/// </para>
/// <para>
/// <b>There is deliberately no <c>CreatorId</c> here.</b> The client never needs to know who owns a document,
/// only what it may do with it — exposing the uploader would add a personal-data field to every list row to
/// answer a question the six booleans already answer.
/// </para>
/// <para>
/// Computed once per distinct (type, is-owner) pair on a page and mapped onto the rows, so the cost is bounded by
/// the page's distinct types rather than by its row count, and reads the same per-request access memo the
/// enforcement points read.
/// </para>
/// </summary>
public class DocumentRightsDto
{
    /// <summary>Detail, blob download, and membership of any list this caller asks for.</summary>
    public bool CanRead { get; set; }

    /// <summary>
    /// The operator edit family: confirm / reclassify (the <b>target</b> type of which is a separate judgment the
    /// type picker answers), correct Markdown, edit field values, re-parse, re-extract, and re-file into a
    /// cabinet.
    /// </summary>
    public bool CanEdit { get; set; }

    /// <summary>
    /// Allow a suspected duplicate, resolve field validation warnings, reject review. Narrower than
    /// <see cref="CanEdit"/> on purpose: these clear a blocking review reason, so an uploader is not admitted to
    /// them on their own document.
    /// </summary>
    public bool CanReview { get; set; }

    /// <summary>Soft delete into the recycle bin.</summary>
    public bool CanDelete { get; set; }

    /// <summary>Restore out of the recycle bin. Whoever may delete may undo.</summary>
    public bool CanRestore { get; set; }

    /// <summary>Retry a failed pipeline run on this document.</summary>
    public bool CanRetry { get; set; }
}
