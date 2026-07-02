using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace Dignite.Vault.Extract.Parse.OpenXml;

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
    private const string NsC = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string WpsUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string WpgUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

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

    /// <summary>A list item: text, zero-based nesting level, and whether the list is ordered (vs a bullet).</summary>
    public sealed record ListItemSpec(string Text, int Level, bool Ordered) : BlockSpec;

    /// <summary>A list item on a numbering definition that only defines level 0 (to exercise the dangling-ilvl fallback).</summary>
    public sealed record DanglingLevelSpec(string Text, int Level) : BlockSpec;

    /// <summary>A chart: title + category labels + a single named series of values (aligned to the categories).</summary>
    public sealed record ChartSpec(string Title, IReadOnlyList<string> Categories, string SeriesName, IReadOnlyList<string> Values) : BlockSpec;

    /// <summary>A single-cell table whose cell contains an embedded image (to exercise figure-in-cell extraction).</summary>
    public sealed record TableImageCellSpec(ImageSpec Image) : BlockSpec;

    /// <summary>A block-level content control (w:sdt) wrapping a paragraph (to exercise sdt recursion).</summary>
    public sealed record ContentControlSpec(string Text) : BlockSpec;

    /// <summary>A paragraph carrying a legacy VML raster image (w:pict/v:imagedata) instead of DrawingML.</summary>
    public sealed record VmlImageSpec(ImageSpec Image) : BlockSpec;

    /// <summary>A single-cell table whose cell wraps its paragraph in a content control (w:sdt).</summary>
    public sealed record TableContentControlCellSpec(string Text) : BlockSpec;

    /// <summary>A stack of nested content controls (w:sdt) Depth levels deep wrapping one paragraph (to exercise the depth cap).</summary>
    public sealed record DeeplyNestedSdtSpec(int Depth, string Text) : BlockSpec;

    /// <summary>A modern DrawingML text box (wps:txbx) containing a paragraph of text AND an embedded image.</summary>
    public sealed record TextBoxImageSpec(string Text, ImageSpec Image) : BlockSpec;

    /// <summary>
    /// A paragraph carrying a footnote / endnote reference after its text (#315). <c>NoteText</c> null = a
    /// DANGLING reference (no matching note body); when it is the only note of its kind the notes part is not
    /// created either (the missing-part case). The notes part, when created, also carries the auto-inserted
    /// separator / continuationSeparator notes so the extractor's separator exclusion is exercised.
    /// </summary>
    public sealed record NoteSpec(string ParagraphText, int Id, bool IsEndnote, string? NoteText) : BlockSpec;

    /// <summary>A paragraph carrying a single grouped drawing (wpg:wgp) with several pictures (#322).</summary>
    public sealed record GroupedImagesSpec(IReadOnlyList<ImageSpec> Images) : BlockSpec;

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

        public DocSpec BulletItem(string text, int level = 0)
        {
            Blocks.Add(new ListItemSpec(text, level, Ordered: false));
            return this;
        }

        public DocSpec OrderedItem(string text, int level = 0)
        {
            Blocks.Add(new ListItemSpec(text, level, Ordered: true));
            return this;
        }

        public DocSpec DanglingLevelItem(string text, int level)
        {
            Blocks.Add(new DanglingLevelSpec(text, level));
            return this;
        }

        public DocSpec Chart(string title, IReadOnlyList<string> categories, string seriesName, IReadOnlyList<string> values)
        {
            Blocks.Add(new ChartSpec(title, categories, seriesName, values));
            return this;
        }

        public DocSpec TableWithImageInCell(ImageSpec image)
        {
            Blocks.Add(new TableImageCellSpec(image));
            return this;
        }

        public DocSpec ContentControl(string text)
        {
            Blocks.Add(new ContentControlSpec(text));
            return this;
        }

        public DocSpec VmlImage(ImageSpec image)
        {
            Blocks.Add(new VmlImageSpec(image));
            return this;
        }

        public DocSpec TableWithContentControlCell(string text)
        {
            Blocks.Add(new TableContentControlCellSpec(text));
            return this;
        }

        public DocSpec DeeplyNestedContentControl(int depth, string text)
        {
            Blocks.Add(new DeeplyNestedSdtSpec(depth, text));
            return this;
        }

        public DocSpec TextBoxWithImage(string text, ImageSpec image)
        {
            Blocks.Add(new TextBoxImageSpec(text, image));
            return this;
        }

        /// <summary>A paragraph whose text is followed by a footnote reference; the note body is registered in the FootnotesPart.</summary>
        public DocSpec Footnote(string paragraphText, int id, string noteText)
        {
            Blocks.Add(new NoteSpec(paragraphText, id, IsEndnote: false, noteText));
            return this;
        }

        /// <summary>A paragraph whose text is followed by an endnote reference; the note body is registered in the EndnotesPart.</summary>
        public DocSpec Endnote(string paragraphText, int id, string noteText)
        {
            Blocks.Add(new NoteSpec(paragraphText, id, IsEndnote: true, noteText));
            return this;
        }

        /// <summary>A footnote reference whose id has no matching note body — dangling if a FootnotesPart exists, missing-part if not.</summary>
        public DocSpec DanglingFootnote(string paragraphText, int id)
        {
            Blocks.Add(new NoteSpec(paragraphText, id, IsEndnote: false, NoteText: null));
            return this;
        }

        /// <summary>A single grouped drawing (wpg:wgp) containing several pictures, to exercise the pic:pic walk (#322).</summary>
        public DocSpec GroupedImages(params ImageSpec[] images)
        {
            Blocks.Add(new GroupedImagesSpec(images));
            return this;
        }
    }

    public static byte[] Build(DocSpec spec)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();

            // A list item references a numbering definition; create one (numId 1 = bullet, numId 2 = ordered)
            // only when the document actually contains list items.
            if (spec.Blocks.OfType<ListItemSpec>().Any())
            {
                var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
                numberingPart.Numbering = new Numbering(NumberingXml());
                numberingPart.Numbering.Save();
            }

            var body = new StringBuilder();
            var imageRel = 0;
            var hyperlinkRel = 0;
            var chartRel = 0;
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

                    case ListItemSpec listItem:
                        body.Append(ListItemXml(listItem));
                        break;

                    case DanglingLevelSpec dangling:
                        body.Append(ListItemParagraphXml(dangling.Text, dangling.Level, numId: 3));
                        break;

                    case ChartSpec chart:
                        var chartRelId = $"rIdChart{chartRel++}";
                        var chartPart = mainPart.AddNewPart<ChartPart>(chartRelId);
                        chartPart.ChartSpace = new C.ChartSpace(ChartSpaceXml(chart));
                        chartPart.ChartSpace.Save();
                        body.Append(ChartDrawingXml(chartRelId));
                        break;

                    case TableImageCellSpec tableImage:
                        body.Append(TableImageCellXml(tableImage.Image, AddImage(mainPart, tableImage.Image, ref imageRel)));
                        break;

                    case ContentControlSpec contentControl:
                        body.Append(ContentControlXml(contentControl.Text));
                        break;

                    case VmlImageSpec vmlImage:
                        body.Append(VmlImageXml(AddImage(mainPart, vmlImage.Image, ref imageRel)));
                        break;

                    case TableContentControlCellSpec tableCc:
                        body.Append(TableContentControlCellXml(tableCc.Text));
                        break;

                    case DeeplyNestedSdtSpec deepSdt:
                        body.Append(DeeplyNestedSdtXml(deepSdt.Depth, deepSdt.Text));
                        break;

                    case TextBoxImageSpec textBoxImage:
                        body.Append(TextBoxImageXml(textBoxImage.Text, textBoxImage.Image, AddImage(mainPart, textBoxImage.Image, ref imageRel)));
                        break;

                    case NoteSpec note:
                        body.Append(NoteReferenceParagraphXml(note));
                        break;

                    case GroupedImagesSpec grouped:
                        body.Append(GroupedImagesXml(
                            grouped.Images,
                            grouped.Images.Select(img => AddImage(mainPart, img, ref imageRel)).ToList()));
                        break;
                }
            }

            var footnoteBodies = spec.Blocks.OfType<NoteSpec>().Where(n => !n.IsEndnote && n.NoteText is not null).ToList();
            if (footnoteBodies.Count > 0)
            {
                var footnotesPart = mainPart.AddNewPart<FootnotesPart>();
                footnotesPart.Footnotes = new Footnotes(FootnotesXml(footnoteBodies));
                footnotesPart.Footnotes.Save();
            }

            var endnoteBodies = spec.Blocks.OfType<NoteSpec>().Where(n => n.IsEndnote && n.NoteText is not null).ToList();
            if (endnoteBodies.Count > 0)
            {
                var endnotesPart = mainPart.AddNewPart<EndnotesPart>();
                endnotesPart.Endnotes = new Endnotes(EndnotesXml(endnoteBodies));
                endnotesPart.Endnotes.Save();
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

    private static string NoteReferenceParagraphXml(NoteSpec note)
    {
        var reference = note.IsEndnote
            ? $"<w:endnoteReference w:id=\"{note.Id}\"/>"
            : $"<w:footnoteReference w:id=\"{note.Id}\"/>";
        // Text run, then a separate run carrying only the reference (as Word authors it).
        return $"<w:p><w:r><w:t xml:space=\"preserve\">{Escape(note.ParagraphText)}</w:t></w:r><w:r>{reference}</w:r></w:p>";
    }

    private static string FootnotesXml(IEnumerable<NoteSpec> notes)
    {
        // Real Word documents carry auto-inserted separator / continuationSeparator notes; include them (with
        // sentinel text) so the extractor's separator exclusion is exercised. Author notes follow.
        var bodies = string.Concat(notes.Select(n =>
            $"<w:footnote w:id=\"{n.Id}\"><w:p><w:r><w:t xml:space=\"preserve\">{Escape(n.NoteText!)}</w:t></w:r></w:p></w:footnote>"));
        return $"""
                <w:footnotes xmlns:w="{NsW}">
                  <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:t xml:space="preserve">SEP_SENTINEL</w:t></w:r></w:p></w:footnote>
                  <w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:t xml:space="preserve">CONT_SENTINEL</w:t></w:r></w:p></w:footnote>
                  {bodies}
                </w:footnotes>
                """;
    }

    private static string EndnotesXml(IEnumerable<NoteSpec> notes)
    {
        var bodies = string.Concat(notes.Select(n =>
            $"<w:endnote w:id=\"{n.Id}\"><w:p><w:r><w:t xml:space=\"preserve\">{Escape(n.NoteText!)}</w:t></w:r></w:p></w:endnote>"));
        return $"""
                <w:endnotes xmlns:w="{NsW}">
                  <w:endnote w:type="separator" w:id="-1"><w:p><w:r><w:t xml:space="preserve">SEP_SENTINEL</w:t></w:r></w:p></w:endnote>
                  <w:endnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:t xml:space="preserve">CONT_SENTINEL</w:t></w:r></w:p></w:endnote>
                  {bodies}
                </w:endnotes>
                """;
    }

    private static string ImageParagraphXml(ImageSpec image, string relId) =>
        $"<w:p><w:r>{InlineDrawingXml(image, relId)}</w:r></w:p>";

    /// <summary>
    /// One inline drawing whose graphic is a wpg:wgp GROUP of several pic:pic pictures (each with its own
    /// blip + a:ext). The old FirstOrDefault-blip walk transcribed only the first picture; the pic:pic walk
    /// (#322) transcribes each. The group's wp:inline carries a large extent so the pictures are not
    /// decorative-filtered.
    /// </summary>
    private static string GroupedImagesXml(IReadOnlyList<ImageSpec> images, IReadOnlyList<string> relIds)
    {
        var pics = string.Concat(images.Select((image, i) =>
        {
            var descr = image.AltText is { Length: > 0 } ? $" descr=\"{Escape(image.AltText)}\"" : string.Empty;
            return $"<pic:pic><pic:nvPicPr><pic:cNvPr id=\"{i + 1}\" name=\"GroupImg{i}\"{descr}/><pic:cNvPicPr/></pic:nvPicPr>"
                 + $"<pic:blipFill><a:blip r:embed=\"{relIds[i]}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
                 + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{image.ExtentCx}\" cy=\"{image.ExtentCy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>";
        }));

        return $"""
                <w:p><w:r><w:drawing>
                  <wp:inline>
                    <wp:extent cx="4000000" cy="2000000"/>
                    <wp:docPr id="500" name="Group 1"/>
                    <a:graphic>
                      <a:graphicData uri="{WpgUri}">
                        <wpg:wgp xmlns:wpg="{WpgUri}">
                          <wpg:cNvGrpSpPr/>
                          <wpg:grpSpPr/>
                          {pics}
                        </wpg:wgp>
                      </a:graphicData>
                    </a:graphic>
                  </wp:inline>
                </w:drawing></w:r></w:p>
                """;
    }

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

    private static string ContentControlXml(string text) =>
        $"<w:sdt><w:sdtPr/><w:sdtContent><w:p><w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p></w:sdtContent></w:sdt>";

    private static string TableContentControlCellXml(string text) =>
        $"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:sdt><w:sdtPr/><w:sdtContent><w:p><w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p></w:sdtContent></w:sdt></w:tc></w:tr></w:tbl>";

    private static string TextBoxImageXml(string text, ImageSpec image, string relId) =>
        $"""
         <w:p><w:r><w:drawing>
           <wp:inline>
             <wp:extent cx="2000000" cy="2000000"/>
             <wp:docPr id="60" name="TextBox 60"/>
             <a:graphic>
               <a:graphicData uri="{WpsUri}">
                 <wps:wsp>
                   <wps:txbx><w:txbxContent>
                     <w:p><w:r><w:t xml:space="preserve">{Escape(text)}</w:t></w:r></w:p>
                     <w:p><w:r>{InlineDrawingXml(image, relId)}</w:r></w:p>
                   </w:txbxContent></wps:txbx>
                   <wps:bodyPr/>
                 </wps:wsp>
               </a:graphicData>
             </a:graphic>
           </wp:inline>
         </w:drawing></w:r></w:p>
         """;

    private static string DeeplyNestedSdtXml(int depth, string text)
    {
        var inner = $"<w:p><w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>";
        for (var i = 0; i < depth; i++)
        {
            inner = $"<w:sdt><w:sdtPr/><w:sdtContent>{inner}</w:sdtContent></w:sdt>";
        }

        return inner;
    }

    private static string VmlImageXml(string relId) =>
        $"<w:p><w:r><w:pict><v:shape id=\"vml{relId}\" style=\"width:100pt;height:100pt\"><v:imagedata r:id=\"{relId}\"/></v:shape></w:pict></w:r></w:p>";

    private static string TableImageCellXml(ImageSpec image, string relId) =>
        $"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r>{InlineDrawingXml(image, relId)}</w:r></w:p></w:tc></w:tr></w:tbl>";

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

    private static string ChartSpaceXml(ChartSpec chart)
    {
        var title = chart.Title is { Length: > 0 }
            ? $"<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(chart.Title)}</a:t></a:r></a:p></c:rich></c:tx></c:title>"
            : string.Empty;
        var cats = string.Concat(chart.Categories.Select((c, i) => $"<c:pt idx=\"{i}\"><c:v>{Escape(c)}</c:v></c:pt>"));
        var vals = string.Concat(chart.Values.Select((v, i) => $"<c:pt idx=\"{i}\"><c:v>{Escape(v)}</c:v></c:pt>"));

        return $"""
                <c:chartSpace xmlns:c="{NsC}" xmlns:a="{NsA}" xmlns:r="{NsR}">
                  <c:chart>
                    {title}
                    <c:plotArea><c:layout/><c:barChart><c:barDir val="col"/>
                      <c:ser>
                        <c:idx val="0"/><c:order val="0"/>
                        <c:tx><c:strRef><c:f>n</c:f><c:strCache><c:ptCount val="1"/><c:pt idx="0"><c:v>{Escape(chart.SeriesName)}</c:v></c:pt></c:strCache></c:strRef></c:tx>
                        <c:cat><c:strRef><c:f>c</c:f><c:strCache><c:ptCount val="{chart.Categories.Count}"/>{cats}</c:strCache></c:strRef></c:cat>
                        <c:val><c:numRef><c:f>v</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val="{chart.Values.Count}"/>{vals}</c:numCache></c:numRef></c:val>
                      </c:ser>
                    </c:barChart></c:plotArea>
                  </c:chart>
                </c:chartSpace>
                """;
    }

    private static string ChartDrawingXml(string relId) =>
        $"""
         <w:p><w:r><w:drawing>
           <wp:inline>
             <wp:extent cx="4000000" cy="3000000"/>
             <wp:docPr id="200" name="Chart 1"/>
             <a:graphic>
               <a:graphicData uri="{ChartUri}">
                 <c:chart xmlns:c="{NsC}" xmlns:r="{NsR}" r:id="{relId}"/>
               </a:graphicData>
             </a:graphic>
           </wp:inline>
         </w:drawing></w:r></w:p>
         """;

    private static string NumberingXml()
    {
        // Two abstract definitions: id 0 = bullet, id 1 = decimal (ordered); each defines levels 0-8.
        // numId 1 maps to the bullet definition, numId 2 to the ordered one.
        string Levels(string fmt) => string.Concat(Enumerable.Range(0, 9)
            .Select(i => $"<w:lvl w:ilvl=\"{i}\"><w:numFmt w:val=\"{fmt}\"/></w:lvl>"));

        return $"""
                <w:numbering xmlns:w="{NsW}">
                  <w:abstractNum w:abstractNumId="0">{Levels("bullet")}</w:abstractNum>
                  <w:abstractNum w:abstractNumId="1">{Levels("decimal")}</w:abstractNum>
                  <w:abstractNum w:abstractNumId="2"><w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl></w:abstractNum>
                  <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
                  <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
                  <w:num w:numId="3"><w:abstractNumId w:val="2"/></w:num>
                </w:numbering>
                """;
    }

    private static string ListItemXml(ListItemSpec item) =>
        ListItemParagraphXml(item.Text, item.Level, item.Ordered ? 2 : 1);

    private static string ListItemParagraphXml(string text, int level, int numId) =>
        $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/></w:numPr></w:pPr><w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>";

    private static string Escape(string text) => new System.Xml.Linq.XText(text).ToString();
}
