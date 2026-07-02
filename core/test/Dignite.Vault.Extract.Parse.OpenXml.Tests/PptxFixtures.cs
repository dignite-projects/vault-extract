using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Builds minimal-but-valid PPTX packages in memory for the extractor tests. Slides are composed as
/// hand-rolled DrawingML/PresentationML XML (the most direct way to control shape offsets, alt-text,
/// placeholder types, embedded-image relationships, charts, tables, and notes) and attached to real
/// <c>SlidePart</c>s via the OpenXML SDK so <c>PptxExtractor</c> parses exactly what a host would see.
/// </summary>
internal static class PptxFixtures
{
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsC = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    /// <summary>A picture to embed: raster bytes, MIME type, optional alt-text, EMU offset, EMU display size.</summary>
    public sealed record ImageSpec(
        byte[] Bytes,
        string ContentType,
        string? AltText,
        long OffsetX,
        long OffsetY,
        long ExtentCx = 914400,
        long ExtentCy = 914400);

    /// <summary>A text shape: the text, whether it is a title placeholder, and its EMU offset.</summary>
    public sealed record TextSpec(string Text, bool IsTitle, long OffsetX, long OffsetY);

    /// <summary>A chart: title + category labels + named series (each with values aligned to the categories).</summary>
    public sealed record ChartSpec(
        string? Title,
        IReadOnlyList<string> Categories,
        IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Series,
        long OffsetX = 100,
        long OffsetY = 5_000_000,
        // A value-axis title; used to verify ReadTitle ignores axis titles when the chart title is absent.
        string? AxisTitle = null,
        // When true, the cached c:pt elements omit their idx attribute (some generators do), to verify the
        // positional fallback keeps every point.
        bool OmitPointIdx = false,
        // When set, the SECOND series emits these category labels instead of the shared ones — used to
        // build a divergent-category-axis chart (different label at the same index) that must not render,
        // or a union case (extra non-conflicting labels) that must.
        IReadOnlyList<string>? SecondSeriesCategories = null,
        // When true, the FIRST series omits its c:cat cache entirely (categories must seed from a later
        // series instead of the first).
        bool FirstSeriesOmitsCategories = false,
        // When set, the category axis is a two-level (c:multiLvlStrRef) axis: each tuple is (outer, inner) for
        // that position. Verifies multi-level axes compose compound labels ("2024 / Q1") rather than colliding
        // idx values across levels (#321). Takes precedence over the single-level Categories.
        IReadOnlyList<(string Outer, string Inner)>? TwoLevelCategories = null);

    /// <summary>A native table: rows of cells (first row is the header), at an EMU offset.</summary>
    public sealed record TableSpec(IReadOnlyList<IReadOnlyList<string>> Rows, long OffsetX = 100, long OffsetY = 6_000_000);

    public sealed class SlideSpec
    {
        public List<TextSpec> Texts { get; } = new();
        public List<ImageSpec> Images { get; } = new();
        public List<ImageSpec> GroupedImages { get; } = new();
        public List<ChartSpec> Charts { get; } = new();
        public List<TableSpec> Tables { get; } = new();
        public string? Notes { get; set; }

        /// <summary>The cached display value of a slide-number placeholder field (p:ph type="sldNum" + a:fld), if any.</summary>
        public string? SlideNumberFieldText { get; set; }

        /// <summary>Adds a slide-number placeholder shape carrying a cached field value, to verify it is excluded from the text payload.</summary>
        public SlideSpec WithSlideNumberField(string cachedValue)
        {
            SlideNumberFieldText = cachedValue;
            return this;
        }

        /// <summary>Adds an image nested inside a grouped shape (p:grpSp), to exercise group recursion.</summary>
        public SlideSpec ImageInGroup(ImageSpec image)
        {
            GroupedImages.Add(image);
            return this;
        }

        public List<(string Line1, string Line2, long X, long Y)> SoftBreakTexts { get; } = new();

        public SlideSpec Text(string text, bool isTitle = false, long x = 100, long y = 100)
        {
            Texts.Add(new TextSpec(text, isTitle, x, y));
            return this;
        }

        /// <summary>Adds a text shape whose single paragraph contains an a:br soft line break between the two runs.</summary>
        public SlideSpec TextWithSoftBreak(string line1, string line2, long x = 100, long y = 100)
        {
            SoftBreakTexts.Add((line1, line2, x, y));
            return this;
        }

        public SlideSpec Image(ImageSpec image)
        {
            Images.Add(image);
            return this;
        }

        public SlideSpec Chart(ChartSpec chart)
        {
            Charts.Add(chart);
            return this;
        }

        public SlideSpec Table(TableSpec table)
        {
            Tables.Add(table);
            return this;
        }

        public SlideSpec WithNotes(string notes)
        {
            Notes = notes;
            return this;
        }

        /// <summary>Text shapes wrapped in an mc:AlternateContent fork (#319), added via <see cref="McAlternateContentText"/>.</summary>
        public List<string> McTexts { get; } = new();

        /// <summary>
        /// Adds a text shape wrapped in an <c>mc:AlternateContent</c> fork (a Choice + a Fallback carrying the
        /// same text), to verify the extractor collapses the markup-compatibility fork on open (#319) instead of
        /// skipping the AlternateContent node in the typed shape walk.
        /// </summary>
        public SlideSpec McAlternateContentText(string text)
        {
            McTexts.Add(text);
            return this;
        }

        /// <summary>Caption + image pairs inside one grouped shape (#313), added via <see cref="GroupedCaptionAndImage"/>.</summary>
        public List<(string Caption, long CaptionY, ImageSpec Image)> CaptionImageGroups { get; } = new();

        /// <summary>
        /// Adds a grouped shape containing a caption text (authored FIRST) at <paramref name="captionY"/> and an
        /// image (authored SECOND) at the image's own offset, to verify group-transform reading order (#313):
        /// the two must sort by their own positions within the group, not by XML/encounter order.
        /// </summary>
        public SlideSpec GroupedCaptionAndImage(string caption, long captionY, ImageSpec image)
        {
            CaptionImageGroups.Add((caption, captionY, image));
            return this;
        }

        /// <summary>A slide body placeholder that omits its own xfrm (position inherited from the layout, #313).</summary>
        public string? InheritedBodyText { get; private set; }

        /// <summary>The Y offset (EMU) the layout's body placeholder carries — the position the slide's no-xfrm body placeholder should inherit (#313).</summary>
        public long LayoutBodyOffsetY { get; private set; }

        /// <summary>Adds a slide body placeholder with no xfrm, and a slide layout whose matching body
        /// placeholder sits at <paramref name="layoutBodyY"/>, to verify layout-inherited position (#313).</summary>
        public SlideSpec WithLayoutInheritedBody(string text, long layoutBodyY)
        {
            InheritedBodyText = text;
            LayoutBodyOffsetY = layoutBodyY;
            return this;
        }
    }

    public static byte[] Build(params SlideSpec[] slides)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            var slideIdList = new SlideIdList();

            uint slideId = 256;
            var index = 0;
            foreach (var slide in slides)
            {
                var relId = $"rIdSlide{index}";
                var slidePart = presentationPart.AddNewPart<SlidePart>(relId);
                PopulateSlide(slidePart, slide, index);

                slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = relId });
                index++;
            }

            presentationPart.Presentation = new Presentation(slideIdList);
            presentationPart.Presentation.Save();
        }

        return stream.ToArray();
    }

    private static void PopulateSlide(SlidePart slidePart, SlideSpec slide, int slideIndex)
    {
        var shapes = new StringBuilder();
        var shapeId = 2u;

        foreach (var text in slide.Texts)
        {
            shapes.Append(TextShapeXml(shapeId++, text));
        }

        foreach (var mcText in slide.McTexts)
        {
            shapes.Append(McAlternateContentShapeXml(shapeId++, mcText));
        }

        foreach (var (line1, line2, x, y) in slide.SoftBreakTexts)
        {
            shapes.Append(SoftBreakShapeXml(shapeId++, line1, line2, x, y));
        }

        var imageRel = 0;
        foreach (var image in slide.Images)
        {
            var relId = $"rIdImg{slideIndex}_{imageRel++}";
            var imagePart = slidePart.AddNewPart<ImagePart>(image.ContentType, relId);
            using (var s = imagePart.GetStream(FileMode.Create, FileAccess.Write))
            {
                s.Write(image.Bytes, 0, image.Bytes.Length);
            }

            shapes.Append(PictureXml(shapeId++, image, relId));
        }

        var chartRel = 0;
        foreach (var chart in slide.Charts)
        {
            var relId = $"rIdChart{slideIndex}_{chartRel++}";
            var chartPart = slidePart.AddNewPart<ChartPart>(relId);
            chartPart.ChartSpace = new C.ChartSpace(ChartSpaceXml(chart));
            shapes.Append(ChartFrameXml(shapeId++, chart, relId));
        }

        foreach (var table in slide.Tables)
        {
            shapes.Append(TableFrameXml(shapeId++, table));
        }

        if (slide.SlideNumberFieldText is { Length: > 0 } slideNumber)
        {
            shapes.Append(SlideNumberFieldXml(shapeId++, slideNumber));
        }

        if (slide.GroupedImages.Count > 0)
        {
            var groupShapes = new StringBuilder();
            foreach (var image in slide.GroupedImages)
            {
                var relId = $"rIdGrpImg{slideIndex}_{imageRel++}";
                var imagePart = slidePart.AddNewPart<ImagePart>(image.ContentType, relId);
                using (var s = imagePart.GetStream(FileMode.Create, FileAccess.Write))
                {
                    s.Write(image.Bytes, 0, image.Bytes.Length);
                }

                groupShapes.Append(PictureXml(shapeId++, image, relId));
            }

            shapes.Append(GroupXml(shapeId++, groupShapes.ToString()));
        }

        foreach (var (caption, captionY, image) in slide.CaptionImageGroups)
        {
            var relId = $"rIdCapImg{slideIndex}_{imageRel++}";
            var imagePart = slidePart.AddNewPart<ImagePart>(image.ContentType, relId);
            using (var s = imagePart.GetStream(FileMode.Create, FileAccess.Write))
            {
                s.Write(image.Bytes, 0, image.Bytes.Length);
            }

            var groupId = shapeId++;
            var captionId = shapeId++;
            var picId = shapeId++;
            shapes.Append(CaptionImageGroupXml(groupId, captionId, caption, captionY, picId, image, relId));
        }

        if (slide.InheritedBodyText is { Length: > 0 } inheritedBody)
        {
            shapes.Append(InheritedBodyPlaceholderXml(shapeId++, inheritedBody));
            var layoutPart = slidePart.AddNewPart<SlideLayoutPart>();
            layoutPart.SlideLayout = new SlideLayout(SlideLayoutXml(slide.LayoutBodyOffsetY));
            layoutPart.SlideLayout.Save();
        }

        slidePart.Slide = new Slide(SlideXml(shapes.ToString()));

        if (slide.Notes is { Length: > 0 })
        {
            var notesPart = slidePart.AddNewPart<NotesSlidePart>($"rIdNotes{slideIndex}");
            notesPart.NotesSlide = new NotesSlide(NotesXml(slide.Notes));
        }
    }

    private static string SlideXml(string shapes) =>
        $"""
         <p:sld xmlns:p="{NsP}" xmlns:a="{NsA}" xmlns:r="{NsR}">
           <p:cSld><p:spTree>
             <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
             <p:grpSpPr/>
             {shapes}
           </p:spTree></p:cSld>
         </p:sld>
         """;

    private static string GroupXml(uint id, string childShapes) =>
        $"""
         <p:grpSp>
           <p:nvGrpSpPr><p:cNvPr id="{id}" name="Group{id}"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
           <p:grpSpPr><a:xfrm>
             <a:off x="500000" y="500000"/><a:ext cx="3000000" cy="3000000"/>
             <a:chOff x="0" y="0"/><a:chExt cx="3000000" cy="3000000"/>
           </a:xfrm></p:grpSpPr>
           {childShapes}
         </p:grpSp>
         """;

    /// <summary>
    /// A grouped shape with a caption text (authored first) at <paramref name="captionY"/> and a picture
    /// (authored second) at the image's own offset. The group carries its own <c>a:off</c>/<c>a:chOff</c>, so
    /// the extractor must translate each child by the group offset and order them by their own positions (#313).
    /// </summary>
    private static string CaptionImageGroupXml(uint groupId, uint captionId, string caption, long captionY, uint picId, ImageSpec image, string relId) =>
        $"""
         <p:grpSp>
           <p:nvGrpSpPr><p:cNvPr id="{groupId}" name="Group{groupId}"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
           <p:grpSpPr><a:xfrm>
             <a:off x="500000" y="500000"/><a:ext cx="6000000" cy="6000000"/>
             <a:chOff x="0" y="0"/><a:chExt cx="6000000" cy="6000000"/>
           </a:xfrm></p:grpSpPr>
           <p:sp>
             <p:nvSpPr><p:cNvPr id="{captionId}" name="Caption{captionId}"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
             <p:spPr><a:xfrm><a:off x="100" y="{captionY}"/><a:ext cx="3000000" cy="500000"/></a:xfrm></p:spPr>
             <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(caption)}</a:t></a:r></a:p></p:txBody>
           </p:sp>
           {PictureXml(picId, image, relId)}
         </p:grpSp>
         """;

    /// <summary>A slide body placeholder (type=body, idx=1) with NO xfrm — its position is layout-inherited (#313).</summary>
    private static string InheritedBodyPlaceholderXml(uint id, string text) =>
        $"""
         <p:sp>
           <p:nvSpPr><p:cNvPr id="{id}" name="Body {id}"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
           <p:spPr/>
           <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(text)}</a:t></a:r></a:p></p:txBody>
         </p:sp>
         """;

    /// <summary>A minimal slide layout whose body placeholder (type=body, idx=1) carries an xfrm at the given Y.</summary>
    private static string SlideLayoutXml(long bodyY) =>
        $"""
         <p:sldLayout xmlns:p="{NsP}" xmlns:a="{NsA}" xmlns:r="{NsR}" type="obj">
           <p:cSld><p:spTree>
             <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
             <p:grpSpPr/>
             <p:sp>
               <p:nvSpPr><p:cNvPr id="2" name="Body Placeholder"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
               <p:spPr><a:xfrm><a:off x="838200" y="{bodyY}"/><a:ext cx="7772400" cy="4351338"/></a:xfrm></p:spPr>
               <p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody>
             </p:sp>
           </p:spTree></p:cSld>
         </p:sldLayout>
         """;

    private static string TextShapeXml(uint id, TextSpec text)
    {
        var placeholder = text.IsTitle ? "<p:ph type=\"title\"/>" : string.Empty;
        var paragraphs = string.Concat(text.Text.Split('\n').Select(line =>
            $"<a:p><a:r><a:t>{Escape(line)}</a:t></a:r></a:p>"));
        return $"""
                <p:sp>
                  <p:nvSpPr><p:cNvPr id="{id}" name="Text{id}"/><p:cNvSpPr/><p:nvPr>{placeholder}</p:nvPr></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="{text.OffsetX}" y="{text.OffsetY}"/><a:ext cx="3000000" cy="500000"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody>
                </p:sp>
                """;
    }

    private static string SlideNumberFieldXml(uint id, string cachedValue) =>
        $"""
         <p:sp>
           <p:nvSpPr><p:cNvPr id="{id}" name="Slide Number Placeholder"/><p:cNvSpPr/><p:nvPr><p:ph type="sldNum" idx="10"/></p:nvPr></p:nvSpPr>
           <p:spPr><a:xfrm><a:off x="8000000" y="6500000"/><a:ext cx="1000000" cy="300000"/></a:xfrm></p:spPr>
           <p:txBody><a:bodyPr/><a:lstStyle/>
             <a:p><a:fld id="slidenum1" type="slidenum"><a:t>{Escape(cachedValue)}</a:t></a:fld></a:p>
           </p:txBody>
         </p:sp>
         """;

    private static string SoftBreakShapeXml(uint id, string line1, string line2, long x, long y) =>
        $"""
         <p:sp>
           <p:nvSpPr><p:cNvPr id="{id}" name="Break{id}"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
           <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="3000000" cy="500000"/></a:xfrm></p:spPr>
           <p:txBody><a:bodyPr/><a:lstStyle/>
             <a:p><a:r><a:t>{Escape(line1)}</a:t></a:r><a:br/><a:r><a:t>{Escape(line2)}</a:t></a:r></a:p>
           </p:txBody>
         </p:sp>
         """;

    private static string McAlternateContentShapeXml(uint id, string text)
    {
        // A shape PowerPoint wraps in a markup-compatibility fork: the Choice requires a modern namespace, the
        // Fallback is the legacy equivalent. Both branches carry the same text so the assertion holds whichever
        // branch the SDK selects when it collapses the markup on open. As a direct spTree child this is an
        // <mc:AlternateContent> element — none of the typed shape cases — so without collapsing it is skipped.
        string Sp(uint spId) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"{spId}\" name=\"Mc{spId}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
            $"<p:spPr><a:xfrm><a:off x=\"100\" y=\"2000000\"/><a:ext cx=\"3000000\" cy=\"500000\"/></a:xfrm></p:spPr>" +
            $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(text)}</a:t></a:r></a:p></p:txBody></p:sp>";

        return $"""
                <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
                  <mc:Choice xmlns:a14="http://schemas.microsoft.com/office/drawing/2010/main" Requires="a14">
                    {Sp(id)}
                  </mc:Choice>
                  <mc:Fallback>
                    {Sp(id + 1000)}
                  </mc:Fallback>
                </mc:AlternateContent>
                """;
    }

    private static string PictureXml(uint id, ImageSpec image, string relId)
    {
        var descr = image.AltText is { Length: > 0 } ? $" descr=\"{Escape(image.AltText)}\"" : string.Empty;
        return $"""
                <p:pic>
                  <p:nvPicPr><p:cNvPr id="{id}" name="Pic{id}"{descr}/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                  <p:blipFill><a:blip r:embed="{relId}"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>
                  <p:spPr><a:xfrm><a:off x="{image.OffsetX}" y="{image.OffsetY}"/><a:ext cx="{image.ExtentCx}" cy="{image.ExtentCy}"/></a:xfrm></p:spPr>
                </p:pic>
                """;
    }

    private static string ChartFrameXml(uint id, ChartSpec chart, string relId) =>
        $"""
         <p:graphicFrame>
           <p:nvGraphicFramePr><p:cNvPr id="{id}" name="Chart{id}"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
           <p:xfrm><a:off x="{chart.OffsetX}" y="{chart.OffsetY}"/><a:ext cx="4000000" cy="3000000"/></p:xfrm>
           <a:graphic><a:graphicData uri="{ChartUri}">
             <c:chart xmlns:c="{NsC}" xmlns:r="{NsR}" r:id="{relId}"/>
           </a:graphicData></a:graphic>
         </p:graphicFrame>
         """;

    private static string TableFrameXml(uint id, TableSpec table)
    {
        var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count);
        var grid = string.Concat(Enumerable.Repeat("<a:gridCol w=\"1000000\"/>", columns));
        var rows = string.Concat(table.Rows.Select(row =>
        {
            var cells = string.Concat(row.Select(cell =>
                $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(cell)}</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>"));
            return $"<a:tr h=\"370000\">{cells}</a:tr>";
        }));

        return $"""
                <p:graphicFrame>
                  <p:nvGraphicFramePr><p:cNvPr id="{id}" name="Table{id}"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
                  <p:xfrm><a:off x="{table.OffsetX}" y="{table.OffsetY}"/><a:ext cx="4000000" cy="2000000"/></p:xfrm>
                  <a:graphic><a:graphicData uri="{TableUri}">
                    <a:tbl><a:tblPr/><a:tblGrid>{grid}</a:tblGrid>{rows}</a:tbl>
                  </a:graphicData></a:graphic>
                </p:graphicFrame>
                """;
    }

    private static string ChartSpaceXml(ChartSpec chart)
    {
        var title = chart.Title is { Length: > 0 }
            ? $"<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(chart.Title)}</a:t></a:r></a:p></c:rich></c:tx></c:title>"
            : string.Empty;

        string Idx(int i) => chart.OmitPointIdx ? string.Empty : $" idx=\"{i}\"";

        string CategoryCache(IReadOnlyList<string> cats)
        {
            var points = string.Concat(cats.Select((cat, i) => $"<c:pt{Idx(i)}><c:v>{Escape(cat)}</c:v></c:pt>"));
            return $"<c:cat><c:strRef><c:f>cats</c:f><c:strCache><c:ptCount val=\"{cats.Count}\"/>{points}</c:strCache></c:strRef></c:cat>";
        }

        string MultiLevelCategoryCache(IReadOnlyList<(string Outer, string Inner)> cats)
        {
            // An empty inner value omits its c:pt entirely — exactly how Excel writes a blank leaf cell (a
            // position that carries only an outer-group label). The extractor must NOT forward-fill the leaf.
            var inner = string.Concat(cats.Select((c, i) =>
                string.IsNullOrEmpty(c.Inner) ? string.Empty : $"<c:pt idx=\"{i}\"><c:v>{Escape(c.Inner)}</c:v></c:pt>"));
            // Outer level: cache a point only where the outer label changes (the group's first index), exactly
            // as Excel writes a multi-level axis — the extractor must forward-fill the label across the group.
            var outer = new StringBuilder();
            string? prev = null;
            for (var i = 0; i < cats.Count; i++)
            {
                if (cats[i].Outer != prev)
                {
                    outer.Append($"<c:pt idx=\"{i}\"><c:v>{Escape(cats[i].Outer)}</c:v></c:pt>");
                    prev = cats[i].Outer;
                }
            }

            // Innermost c:lvl first (per ECMA-376), then the outer level.
            return $"<c:cat><c:multiLvlStrRef><c:f>cats</c:f><c:multiLvlStrCache><c:ptCount val=\"{cats.Count}\"/>"
                 + $"<c:lvl>{inner}</c:lvl><c:lvl>{outer}</c:lvl>"
                 + "</c:multiLvlStrCache></c:multiLvlStrRef></c:cat>";
        }

        var seriesXml = string.Concat(chart.Series.Select((ser, si) =>
        {
            var valuePoints = string.Concat(ser.Values.Select((v, i) =>
                $"<c:pt{Idx(i)}><c:v>{Escape(v)}</c:v></c:pt>"));
            var cats = si == 1 && chart.SecondSeriesCategories is not null ? chart.SecondSeriesCategories : chart.Categories;
            // The first series can be made to omit its category cache so seeding falls to a later series.
            var catXml = si == 0 && chart.FirstSeriesOmitsCategories
                ? string.Empty
                : chart.TwoLevelCategories is not null
                    ? MultiLevelCategoryCache(chart.TwoLevelCategories)
                    : CategoryCache(cats);
            return $"""
                    <c:ser>
                      <c:idx val="{si}"/><c:order val="{si}"/>
                      <c:tx><c:strRef><c:f>name</c:f><c:strCache><c:ptCount val="1"/><c:pt idx="0"><c:v>{Escape(ser.Name)}</c:v></c:pt></c:strCache></c:strRef></c:tx>
                      {catXml}
                      <c:val><c:numRef><c:f>vals</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val="{ser.Values.Count}"/>{valuePoints}</c:numCache></c:numRef></c:val>
                    </c:ser>
                    """;
        }));

        // A value axis carrying its own title — used to verify the chart title reader does NOT pick it up.
        var valueAxis = chart.AxisTitle is { Length: > 0 }
            ? $"<c:valAx><c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Escape(chart.AxisTitle)}</a:t></a:r></a:p></c:rich></c:tx></c:title></c:valAx>"
            : string.Empty;

        return $"""
                <c:chartSpace xmlns:c="{NsC}" xmlns:a="{NsA}" xmlns:r="{NsR}">
                  <c:chart>
                    {title}
                    <c:plotArea><c:layout/><c:barChart><c:barDir val="col"/>{seriesXml}</c:barChart>{valueAxis}</c:plotArea>
                  </c:chart>
                </c:chartSpace>
                """;
    }

    private static string NotesXml(string notes)
    {
        var paragraphs = string.Concat(notes.Split('\n').Select(line =>
            $"<a:p><a:r><a:t>{Escape(line)}</a:t></a:r></a:p>"));
        return $"""
                <p:notes xmlns:p="{NsP}" xmlns:a="{NsA}" xmlns:r="{NsR}">
                  <p:cSld><p:spTree>
                    <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                    <p:grpSpPr/>
                    <p:sp>
                      <p:nvSpPr><p:cNvPr id="2" name="Notes Placeholder"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                      <p:spPr/>
                      <p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody>
                    </p:sp>
                  </p:spTree></p:cSld>
                </p:notes>
                """;
    }

    private static string Escape(string text) => new System.Xml.Linq.XText(text).ToString();
}
