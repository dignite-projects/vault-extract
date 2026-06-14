using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Builds minimal-but-valid DOCX packages in memory for the extractor tests. The body is composed as
/// hand-rolled WordprocessingML/DrawingML XML (the most direct way to control paragraph styles, soft
/// breaks, embedded-image relationships, alt-text, drawing extents, and markup-compatibility
/// (<c>mc:AlternateContent</c>) forks) and attached to a real <c>MainDocumentPart</c> via the OpenXML SDK
/// so <c>DocxExtractor</c> parses exactly what a host would see.
/// </summary>
internal static class DocxFixtures
{
    private const string NsW = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsWp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsPic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string NsMc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string NsWps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string NsV = "urn:schemas-microsoft-com:vml";
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string WpsUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    /// <summary>An image to embed: raster bytes, MIME type, optional alt-text, EMU display extent.</summary>
    public sealed record ImageSpec(
        byte[] Bytes,
        string ContentType,
        string? AltText,
        long ExtentCx = 914400,
        long ExtentCy = 914400);

    public abstract record BlockSpec;

    /// <summary>A paragraph; <c>StyleId</c> set (e.g. "Heading1") makes it a heading. Text may contain '\n' for soft breaks.</summary>
    public sealed record ParagraphSpec(string Text, string? StyleId = null) : BlockSpec;

    /// <summary>A paragraph whose single run contains a <c>w:br</c> soft line break between two text runs.</summary>
    public sealed record SoftBreakSpec(string Line1, string Line2) : BlockSpec;

    /// <summary>A paragraph carrying a single inline drawing (embedded image).</summary>
    public sealed record ImageBlockSpec(ImageSpec Image) : BlockSpec;

    /// <summary>
    /// A paragraph carrying a modern Word text box as an <c>mc:AlternateContent</c>: the <c>mc:Choice</c>
    /// (DrawingML <c>wps:txbx</c>) and the <c>mc:Fallback</c> (legacy VML <c>v:textbox</c>) both contain the
    /// same text — the duplication hazard the extractor's MC-collapsing open settings must defuse.
    /// </summary>
    public sealed record AltTextBoxSpec(string Text) : BlockSpec;

    /// <summary>
    /// A paragraph carrying a picture wrapped in <c>mc:AlternateContent</c> with a blip-bearing
    /// <c>w:drawing</c> in <b>both</b> branches (same relationship id) — the double-OCR hazard the
    /// MC-collapsing open settings must defuse.
    /// </summary>
    public sealed record AltImageSpec(ImageSpec Image) : BlockSpec;

    /// <summary>A native table: rows of cells (first row is the header).</summary>
    public sealed record TableSpec(IReadOnlyList<IReadOnlyList<string>> Rows) : BlockSpec;

    /// <summary>A run with optional bold/italic, for building a formatted paragraph.</summary>
    public sealed record RunSpec(string Text, bool Bold = false, bool Italic = false);

    /// <summary>A paragraph composed of explicitly-formatted runs (to exercise inline emphasis rendering).</summary>
    public sealed record RichParagraphSpec(IReadOnlyList<RunSpec> Runs) : BlockSpec;

    /// <summary>A paragraph containing a single external hyperlink (text + URL).</summary>
    public sealed record HyperlinkParagraphSpec(string Text, string Url) : BlockSpec;

    /// <summary>A paragraph with a normal run, an inserted-revision run (w:ins), and a deleted-revision run (w:del).</summary>
    public sealed record TrackedParagraphSpec(string Before, string Inserted, string Deleted) : BlockSpec;

    /// <summary>A Heading1 paragraph that also anchors an mc:AlternateContent text box (heading text + text-box text).</summary>
    public sealed record HeadingTextBoxSpec(string Heading, string TextBox) : BlockSpec;

    public sealed class DocSpec
    {
        public List<BlockSpec> Blocks { get; } = new();

        public DocSpec Paragraph(string text)
        {
            Blocks.Add(new ParagraphSpec(text));
            return this;
        }

        public DocSpec Heading(string text, int level)
        {
            Blocks.Add(new ParagraphSpec(text, $"Heading{level}"));
            return this;
        }

        /// <summary>Adds a paragraph with an explicit style ID (e.g. "Title") to exercise style mapping directly.</summary>
        public DocSpec StyledParagraph(string text, string styleId)
        {
            Blocks.Add(new ParagraphSpec(text, styleId));
            return this;
        }

        public DocSpec SoftBreak(string line1, string line2)
        {
            Blocks.Add(new SoftBreakSpec(line1, line2));
            return this;
        }

        public DocSpec Image(ImageSpec image)
        {
            Blocks.Add(new ImageBlockSpec(image));
            return this;
        }

        /// <summary>Adds a DrawingML text box duplicated in an mc:Choice/mc:Fallback fork.</summary>
        public DocSpec AlternateContentTextBox(string text)
        {
            Blocks.Add(new AltTextBoxSpec(text));
            return this;
        }

        /// <summary>Adds a picture duplicated (same image part) across an mc:Choice/mc:Fallback fork.</summary>
        public DocSpec AlternateContentImage(ImageSpec image)
        {
            Blocks.Add(new AltImageSpec(image));
            return this;
        }

        public DocSpec Table(IReadOnlyList<IReadOnlyList<string>> rows)
        {
            Blocks.Add(new TableSpec(rows));
            return this;
        }

        public DocSpec Runs(params RunSpec[] runs)
        {
            Blocks.Add(new RichParagraphSpec(runs));
            return this;
        }

        public DocSpec HyperlinkParagraph(string text, string url)
        {
            Blocks.Add(new HyperlinkParagraphSpec(text, url));
            return this;
        }

        public DocSpec TrackedChangesParagraph(string before, string inserted, string deleted)
        {
            Blocks.Add(new TrackedParagraphSpec(before, inserted, deleted));
            return this;
        }

        public DocSpec HeadingWithTextBox(string heading, string textBox)
        {
            Blocks.Add(new HeadingTextBoxSpec(heading, textBox));
            return this;
        }
    }

    public static byte[] Build(DocSpec spec)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();

            var body = new StringBuilder();
            var imageRel = 0;
            var hyperlinkRel = 0;
            foreach (var block in spec.Blocks)
            {
                switch (block)
                {
                    case ParagraphSpec paragraph:
                        body.Append(ParagraphXml(paragraph));
                        break;

                    case SoftBreakSpec softBreak:
                        body.Append(SoftBreakXml(softBreak));
                        break;

                    case ImageBlockSpec imageBlock:
                        body.Append(ImageParagraphXml(imageBlock.Image, AddImage(mainPart, imageBlock.Image, ref imageRel)));
                        break;

                    case AltImageSpec altImage:
                        body.Append(AlternateContentImageXml(altImage.Image, AddImage(mainPart, altImage.Image, ref imageRel)));
                        break;

                    case AltTextBoxSpec altTextBox:
                        body.Append(AlternateContentTextBoxXml(altTextBox.Text));
                        break;

                    case TableSpec tableSpec:
                        body.Append(TableXml(tableSpec));
                        break;

                    case RichParagraphSpec richParagraph:
                        body.Append(RichParagraphXml(richParagraph));
                        break;

                    case HyperlinkParagraphSpec hyperlink:
                        var hlRelId = $"rIdHl{hyperlinkRel++}";
                        mainPart.AddHyperlinkRelationship(new System.Uri(hyperlink.Url), isExternal: true, hlRelId);
                        body.Append(HyperlinkParagraphXml(hyperlink.Text, hlRelId));
                        break;

                    case TrackedParagraphSpec tracked:
                        body.Append(TrackedParagraphXml(tracked));
                        break;

                    case HeadingTextBoxSpec headingTextBox:
                        body.Append(HeadingTextBoxXml(headingTextBox.Heading, headingTextBox.TextBox));
                        break;
                }
            }

            mainPart.Document = new Document(DocumentXml(body.ToString()));
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static string AddImage(MainDocumentPart mainPart, ImageSpec image, ref int imageRel)
    {
        var relId = $"rIdImg{imageRel++}";
        var imagePart = mainPart.AddNewPart<ImagePart>(image.ContentType, relId);
        using var s = imagePart.GetStream(FileMode.Create, FileAccess.Write);
        s.Write(image.Bytes, 0, image.Bytes.Length);
        return relId;
    }

    private static string DocumentXml(string blocks) =>
        $"""
         <w:document xmlns:w="{NsW}" xmlns:r="{NsR}" xmlns:wp="{NsWp}" xmlns:a="{NsA}" xmlns:pic="{NsPic}" xmlns:mc="{NsMc}" xmlns:wps="{NsWps}" xmlns:v="{NsV}">
           <w:body>
             {blocks}
             <w:sectPr/>
           </w:body>
         </w:document>
         """;

    private static string ParagraphXml(ParagraphSpec paragraph)
    {
        var pPr = paragraph.StyleId is { Length: > 0 }
            ? $"<w:pPr><w:pStyle w:val=\"{Escape(paragraph.StyleId)}\"/></w:pPr>"
            : string.Empty;

        // Split text on '\n' into separate runs joined by w:br so a multi-line paragraph/heading carries
        // real line breaks (exercises ParagraphText's br -> '\n' handling).
        var runs = string.Join(
            "<w:r><w:br/></w:r>",
            paragraph.Text.Split('\n').Select(line => $"<w:r><w:t xml:space=\"preserve\">{Escape(line)}</w:t></w:r>"));

        return $"<w:p>{pPr}{runs}</w:p>";
    }

    private static string SoftBreakXml(SoftBreakSpec softBreak) =>
        $"<w:p><w:r><w:t xml:space=\"preserve\">{Escape(softBreak.Line1)}</w:t><w:br/><w:t xml:space=\"preserve\">{Escape(softBreak.Line2)}</w:t></w:r></w:p>";

    private static string ImageParagraphXml(ImageSpec image, string relId) =>
        $"<w:p><w:r>{InlineDrawingXml(image, relId)}</w:r></w:p>";

    /// <summary>A single inline <c>w:drawing</c> carrying a picture blip — the shared building block.</summary>
    private static string InlineDrawingXml(ImageSpec image, string relId) =>
        $"""
         <w:drawing>
           <wp:inline>
             <wp:extent cx="{image.ExtentCx}" cy="{image.ExtentCy}"/>
             <wp:docPr id="1" name="Picture 1"{(image.AltText is { Length: > 0 } ? $" descr=\"{Escape(image.AltText)}\"" : string.Empty)}/>
             <a:graphic>
               <a:graphicData uri="{PictureUri}">
                 <pic:pic>
                   <pic:nvPicPr><pic:cNvPr id="0" name="img"/><pic:cNvPicPr/></pic:nvPicPr>
                   <pic:blipFill><a:blip r:embed="{relId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                   <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{image.ExtentCx}" cy="{image.ExtentCy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                 </pic:pic>
               </a:graphicData>
             </a:graphic>
           </wp:inline>
         </w:drawing>
         """;

    /// <summary>
    /// An AlternateContent fork carrying the SAME picture (same relId) in both branches. With MC collapsing
    /// (Office2019, wps understood) the SDK keeps only the Choice, so the extractor sees one drawing.
    /// </summary>
    private static string AlternateContentImageXml(ImageSpec image, string relId) =>
        $"""
         <w:p><w:r>
           <mc:AlternateContent>
             <mc:Choice Requires="wps">{InlineDrawingXml(image, relId)}</mc:Choice>
             <mc:Fallback>{InlineDrawingXml(image, relId)}</mc:Fallback>
           </mc:AlternateContent>
         </w:r></w:p>
         """;

    /// <summary>
    /// An AlternateContent text box RUN: the same text appears in the DrawingML Choice (wps:txbx) and the
    /// legacy VML Fallback (v:textbox). With MC collapsing the SDK keeps only one branch, so the text appears
    /// once. Shared by the standalone-paragraph and heading-paragraph text-box fixtures.
    /// </summary>
    private static string AltTextBoxRunXml(string text) =>
        $"""
         <w:r>
           <mc:AlternateContent>
             <mc:Choice Requires="wps">
               <w:drawing>
                 <wp:inline>
                   <wp:extent cx="2000000" cy="1000000"/>
                   <wp:docPr id="100" name="TextBox 1"/>
                   <a:graphic>
                     <a:graphicData uri="{WpsUri}">
                       <wps:wsp>
                         <wps:txbx><w:txbxContent><w:p><w:r><w:t xml:space="preserve">{Escape(text)}</w:t></w:r></w:p></w:txbxContent></wps:txbx>
                         <wps:bodyPr/>
                       </wps:wsp>
                     </a:graphicData>
                   </a:graphic>
                 </wp:inline>
               </w:drawing>
             </mc:Choice>
             <mc:Fallback>
               <w:pict>
                 <v:rect><v:textbox><w:txbxContent><w:p><w:r><w:t xml:space="preserve">{Escape(text)}</w:t></w:r></w:p></w:txbxContent></v:textbox></v:rect>
               </w:pict>
             </mc:Fallback>
           </mc:AlternateContent>
         </w:r>
         """;

    private static string AlternateContentTextBoxXml(string text) => $"<w:p>{AltTextBoxRunXml(text)}</w:p>";

    /// <summary>A Heading1 paragraph anchoring a text box: a heading run followed by the text-box AlternateContent run.</summary>
    private static string HeadingTextBoxXml(string heading, string textBox) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t xml:space=\"preserve\">{Escape(heading)}</w:t></w:r>{AltTextBoxRunXml(textBox)}</w:p>";

    private static string TableXml(TableSpec table)
    {
        var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count);
        var grid = string.Concat(Enumerable.Repeat("<w:gridCol w:w=\"2000\"/>", columns));
        var rows = string.Concat(table.Rows.Select(row =>
        {
            var cells = string.Concat(row.Select(cell =>
                $"<w:tc><w:tcPr/><w:p><w:r><w:t xml:space=\"preserve\">{Escape(cell)}</w:t></w:r></w:p></w:tc>"));
            return $"<w:tr>{cells}</w:tr>";
        }));

        return $"<w:tbl><w:tblPr/><w:tblGrid>{grid}</w:tblGrid>{rows}</w:tbl>";
    }

    private static string RichParagraphXml(RichParagraphSpec spec)
    {
        var runs = string.Concat(spec.Runs.Select(r =>
        {
            var rPr = r.Bold || r.Italic
                ? $"<w:rPr>{(r.Bold ? "<w:b/>" : string.Empty)}{(r.Italic ? "<w:i/>" : string.Empty)}</w:rPr>"
                : string.Empty;
            return $"<w:r>{rPr}<w:t xml:space=\"preserve\">{Escape(r.Text)}</w:t></w:r>";
        }));

        return $"<w:p>{runs}</w:p>";
    }

    private static string HyperlinkParagraphXml(string text, string relId) =>
        $"<w:p><w:hyperlink r:id=\"{relId}\"><w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:hyperlink></w:p>";

    private static string TrackedParagraphXml(TrackedParagraphSpec spec) =>
        $"""
         <w:p>
           <w:r><w:t xml:space="preserve">{Escape(spec.Before)}</w:t></w:r>
           <w:ins w:id="1" w:author="t" w:date="2024-01-01T00:00:00Z"><w:r><w:t xml:space="preserve">{Escape(spec.Inserted)}</w:t></w:r></w:ins>
           <w:del w:id="2" w:author="t" w:date="2024-01-01T00:00:00Z"><w:r><w:delText xml:space="preserve">{Escape(spec.Deleted)}</w:delText></w:r></w:del>
         </w:p>
         """;

    private static string Escape(string text) => new System.Xml.Linq.XText(text).ToString();
}
