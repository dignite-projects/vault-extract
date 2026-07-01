using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Shared OpenXML package open settings for the DOCX/PPTX extractors (#319).
/// </summary>
internal static class OpenXmlPackageSettings
{
    /// <summary>
    /// Open settings that collapse OOXML markup-compatibility (<c>mc:AlternateContent</c>) to its single
    /// selected branch <b>before</b> parsing. Under the default <see cref="MarkupCompatibilityProcessMode.NoProcess"/>
    /// both branches stay in the tree, which harms the two extractors in opposite ways:
    /// <list type="bullet">
    /// <item><b>DOCX</b> walks with <c>Descendants</c>, so it would read the SAME content twice — a modern Word
    /// text box stores its text in both the <c>mc:Choice</c> (DrawingML <c>wps:txbx</c>) and the
    /// <c>mc:Fallback</c> (legacy VML Word auto-writes), and an <c>AlternateContent</c>-wrapped picture can
    /// carry a blip in both branches, duplicating text and double-OCR-ing (and double-charging the image
    /// budget for) one logical figure.</item>
    /// <item><b>PPTX</b> dispatches by strong shape type, so an <c>mc:AlternateContent</c> node — which is none
    /// of <c>P.Picture</c> / <c>P.Shape</c> / <c>P.GraphicFrame</c> / <c>P.GroupShape</c> — is silently skipped
    /// by the typed walk, dropping any shape / picture / text that PowerPoint 2016+ wraps in a compatibility
    /// fork, with no #268 signal (#319).</item>
    /// </list>
    /// Collapsing on open resolves each fork to one real element before either walk runs.
    /// <see cref="FileFormatVersions.Office2019"/> is recent enough that the SDK understands the modern
    /// (<c>wps</c> / DrawingML) namespaces, so it keeps the richer Choice branch (whose <c>w:drawing</c> the
    /// DOCX image path can read) and drops the legacy fallback.
    /// </summary>
    public static readonly OpenSettings McCollapsing = new()
    {
        MarkupCompatibilityProcessSettings = new MarkupCompatibilityProcessSettings(
            MarkupCompatibilityProcessMode.ProcessAllParts,
            FileFormatVersions.Office2019)
    };
}
