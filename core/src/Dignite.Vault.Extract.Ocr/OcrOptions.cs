using System.Collections.Generic;

namespace Dignite.Vault.Extract.Ocr;

public class OcrOptions
{
    /// <summary>Language hint list in BCP 47 format. An empty list means auto-detection.</summary>
    public IList<string> LanguageHints { get; set; } = new List<string>();

    /// <summary>File MIME type, used by some providers to optimize recognition strategy.</summary>
    public string ContentType { get; set; } = string.Empty;
}
