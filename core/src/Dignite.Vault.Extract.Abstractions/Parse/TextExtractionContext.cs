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

    /// <summary>
    /// When <c>true</c> (#477), image-bearing providers surface each embedded figure's source bytes on
    /// <see cref="TextExtractionResult.Figures"/> and inline a <c>figures/{hash}</c> reference into the Markdown,
    /// so the Application layer can persist the image as a blob. Host-deployment-layer toggle threaded down by the
    /// parse job; <b>default <c>false</c></b>, in which case a provider behaves exactly as before (no reference,
    /// no <see cref="TextExtractionResult.Figures"/>). Providers that do not retain images ignore it.
    /// </summary>
    public bool RetainFigureImages { get; set; }
}
