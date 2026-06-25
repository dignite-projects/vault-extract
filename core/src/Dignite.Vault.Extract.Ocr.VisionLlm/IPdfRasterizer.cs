namespace Dignite.Vault.Extract.Ocr.VisionLlm;

/// <summary>
/// Renders PDF pages to raster images so a vision LLM can transcribe scanned / image-only PDFs.
/// <para>
/// Abstracted behind an interface so the provider's multi-page logic can be unit-tested without the
/// native PDFium dependency (tests substitute a fake rasterizer). The production implementation is
/// <see cref="PdfToImageRasterizer"/>.
/// </para>
/// </summary>
public interface IPdfRasterizer
{
    /// <summary>Number of pages in the PDF.</summary>
    int GetPageCount(byte[] pdf);

    /// <summary>Renders the page at <paramref name="pageIndex"/> (0-based) to PNG-encoded bytes.</summary>
    byte[] RenderPageToPng(byte[] pdf, int pageIndex);
}
