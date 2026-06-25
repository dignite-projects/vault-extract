using System.Collections.Generic;

namespace Dignite.Vault.Extract.Ocr;

public class ExtractOcrOptions
{
    /// <summary>Default language hints applied to all OCR requests, in BCP 47 format.</summary>
    public IList<string> DefaultLanguageHints { get; set; } = new List<string> { "ja", "en" };
}
