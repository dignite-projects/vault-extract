using System.IO;
using PDFtoImage;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Ocr.VisionLlm;

/// <summary>
/// Default <see cref="IPdfRasterizer"/> backed by PDFtoImage (PDFium + SkiaSharp).
/// <para>
/// PDFium is not thread-safe; PDFtoImage serialises all native calls with an internal lock, so concurrent
/// document pipelines will queue on PDF rasterization. For the single-document background-job path this is
/// a non-issue (OCR is already a slow sequential external call).
/// </para>
/// </summary>
// [ExposeServices] is REQUIRED: the class name does not end with "PdfRasterizer", so ABP's default
// conventional registration (which exposes IFoo only when the class name ends with "Foo") would register
// the concrete type but NOT the IPdfRasterizer interface — and VisionLlmOcrProvider injects the interface.
// Without this, resolving IOcrProvider throws at runtime once the VisionLlm module is enabled.
[ExposeServices(typeof(IPdfRasterizer))]
public class PdfToImageRasterizer : IPdfRasterizer, ITransientDependency
{
    public virtual int GetPageCount(byte[] pdf)
    {
        return Conversion.GetPageCount(pdf);
    }

    public virtual byte[] RenderPageToPng(byte[] pdf, int pageIndex)
    {
        using var stream = new MemoryStream();
        // SavePng renders the page to an SKBitmap and PNG-encodes it into the stream internally,
        // so this project never touches SkiaSharp types directly. page (System.Index) accepts the
        // 0-based int via implicit conversion.
        Conversion.SavePng(stream, pdf, pageIndex);
        return stream.ToArray();
    }
}
