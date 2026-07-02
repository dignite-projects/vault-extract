namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>Whether a note reference targets a footnote or an endnote (#315). The two id spaces are separate.</summary>
internal enum NoteKind
{
    Footnote,
    Endnote
}

/// <summary>
/// A footnote / endnote reference found in the body at its reading position (#315): the kind and the
/// <c>w:id</c> that keys its body in the <c>FootnotesPart</c> / <c>EndnotesPart</c>. <see cref="Marker"/> is
/// the stable Markdown-footnote label used both for the in-text marker and the appended definition
/// (<c>[^fn2]</c> / <c>[^en1]</c>); the <c>fn</c> / <c>en</c> prefix disambiguates the two separate id spaces.
/// </summary>
internal readonly record struct NoteReference(NoteKind Kind, long Id)
{
    public string Marker => $"[^{(Kind == NoteKind.Footnote ? "fn" : "en")}{Id}]";
}
