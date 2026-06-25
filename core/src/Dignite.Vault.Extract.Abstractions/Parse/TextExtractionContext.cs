using System.Collections.Generic;

namespace Dignite.Vault.Extract.Abstractions.Parse;

public class TextExtractionContext
{
    /// <summary>MIME type passed by the core module from Document.FileOrigin.ContentType.</summary>
    public string ContentType { get; set; } = default!;

    /// <summary>File extension, including the leading dot, such as ".pdf".</summary>
    public string FileExtension { get; set; } = default!;

    /// <summary>Language hints as a BCP 47 list, such as ["ja", "en"].</summary>
    public IList<string> LanguageHints { get; set; } = new List<string>();
}
