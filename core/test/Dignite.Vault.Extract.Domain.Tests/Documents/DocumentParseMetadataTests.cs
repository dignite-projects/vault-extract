using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #218: after <see cref="DocumentParseMetadata"/> / <see cref="NativePayloadManifest"/> inherit
/// ABP <c>ValueObject</c>, they get structural equality exposed through <c>ValueEquals</c>. ABP 10.x
/// ValueObject does not override <c>Equals</c>/<c>GetHashCode</c>/<c>==</c>; structural comparison is the
/// opt-in <c>ValueEquals</c>. This must not break System.Text.Json serialization round-trips, following
/// the same pattern as ExportColumn. Pure unit test; no ABP host required.
/// </summary>
public class DocumentParseMetadataTests
{
    private static NativePayloadManifest CreateManifest() =>
        new("extraction-native/doc-1", "application/json", 1234, "abc123", "PaddleOCR/PP-StructureV3");

    [Fact]
    public void Manifest_With_Same_Values_Should_Be_ValueEqual()
    {
        CreateManifest().ValueEquals(CreateManifest()).ShouldBeTrue();
    }

    [Fact]
    public void Manifest_With_Different_Values_Should_Not_Be_ValueEqual()
    {
        var other = new NativePayloadManifest(
            "extraction-native/doc-1", "application/json", 1234, "DIFFERENT", "PaddleOCR/PP-StructureV3");
        CreateManifest().ValueEquals(other).ShouldBeFalse();
    }

    [Fact]
    public void Metadata_With_Same_Values_Including_Nested_Manifest_Should_Be_ValueEqual()
    {
        var a = new DocumentParseMetadata("PaddleOCR", CreateManifest());
        var b = new DocumentParseMetadata("PaddleOCR", CreateManifest());

        a.ValueEquals(b).ShouldBeTrue();
    }

    [Fact]
    public void Metadata_With_Null_Members_Should_Be_ValueEqual_And_Differ_From_Populated()
    {
        var emptyA = new DocumentParseMetadata(null, null);
        var emptyB = new DocumentParseMetadata(null, null);

        emptyA.ValueEquals(emptyB).ShouldBeTrue();
        emptyA.ValueEquals(new DocumentParseMetadata("PaddleOCR", null)).ShouldBeFalse();
        emptyA.ValueEquals(new DocumentParseMetadata(null, CreateManifest())).ShouldBeFalse();
    }

    [Fact]
    public void Json_Roundtrip_Should_Preserve_Value_Equality()
    {
        var original = new DocumentParseMetadata("PaddleOCR", CreateManifest());

        var json = JsonSerializer.Serialize(original);
        var roundtripped = JsonSerializer.Deserialize<DocumentParseMetadata>(json);

        roundtripped.ShouldNotBeNull();
        roundtripped!.ValueEquals(original).ShouldBeTrue();
    }

    [Fact]
    public void Completeness_Participates_In_Value_Equality_And_Json_Roundtrip()
    {
        var complete = new DocumentParseMetadata("VisionLlm", null);
        var incomplete = new DocumentParseMetadata(
            "VisionLlm", null, isComplete: false, incompleteReason: "2 of 5 page(s) were not fully transcribed.");

        // Completeness participates in structural equality, even when manifest is null.
        complete.ValueEquals(incomplete).ShouldBeFalse();

        var roundtripped = JsonSerializer.Deserialize<DocumentParseMetadata>(
            JsonSerializer.Serialize(incomplete));
        roundtripped.ShouldNotBeNull();
        roundtripped!.IsComplete.ShouldBeFalse();
        roundtripped.IncompleteReason.ShouldBe("2 of 5 page(s) were not fully transcribed.");
        roundtripped.ValueEquals(incomplete).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_Json_Without_Completeness_Deserializes_As_Complete()
    {
        // Backward compatibility (#268): rows persisted before #268 have no completeness fields, so the
        // constructor's optional parameter defaults treat them as complete.
        var legacyJson = "{\"ProviderName\":\"PaddleOCR\",\"NativePayloadManifest\":null}";

        var metadata = JsonSerializer.Deserialize<DocumentParseMetadata>(legacyJson);

        metadata.ShouldNotBeNull();
        metadata!.IsComplete.ShouldBeTrue();
        metadata.IncompleteReason.ShouldBeNull();
    }

    private static FigureManifestEntry CreateFigure(string hash = "fig-hash-1") =>
        new($"extraction-figures/doc-1/{hash}", hash, "image/png", 2048);

    [Fact]
    public void FigureManifestEntry_Has_Structural_Equality()
    {
        CreateFigure().ValueEquals(CreateFigure()).ShouldBeTrue();
        CreateFigure().ValueEquals(CreateFigure("other")).ShouldBeFalse();
    }

    [Fact]
    public void Figures_Participate_In_Value_Equality_Even_When_Native_Manifest_Is_Null()
    {
        // #477: the figures atomics are emitted BEFORE the native-manifest yield break, so they must affect
        // equality even in the common PDF case where NativePayloadManifest is null.
        var withFigure = new DocumentParseMetadata("PdfPig", nativePayloadManifest: null, figures: new[] { CreateFigure() });
        var withoutFigure = new DocumentParseMetadata("PdfPig", nativePayloadManifest: null);

        withFigure.ValueEquals(withoutFigure).ShouldBeFalse(); // one figure vs null figures
        withFigure.ValueEquals(new DocumentParseMetadata("PdfPig", null, figures: new[] { CreateFigure() }))
            .ShouldBeTrue();
        // A different figure hash is a different value.
        withFigure.ValueEquals(new DocumentParseMetadata("PdfPig", null, figures: new[] { CreateFigure("fig-hash-2") }))
            .ShouldBeFalse();
    }

    [Fact]
    public void Empty_Figures_List_Differs_From_Null_Figures()
    {
        var empty = new DocumentParseMetadata("PdfPig", null, figures: System.Array.Empty<FigureManifestEntry>());
        var missing = new DocumentParseMetadata("PdfPig", null);
        empty.ValueEquals(missing).ShouldBeFalse();
    }

    [Fact]
    public void Figures_Survive_Json_Roundtrip()
    {
        var original = new DocumentParseMetadata(
            "PdfPig", CreateManifest(), figures: new[] { CreateFigure("a"), CreateFigure("b") });

        var roundtripped = JsonSerializer.Deserialize<DocumentParseMetadata>(JsonSerializer.Serialize(original));

        roundtripped.ShouldNotBeNull();
        roundtripped!.Figures.ShouldNotBeNull();
        roundtripped.Figures!.Count.ShouldBe(2);
        roundtripped.Figures[0].BlobName.ShouldBe("extraction-figures/doc-1/a");
        roundtripped.Figures[0].ContentType.ShouldBe("image/png");
        roundtripped.ValueEquals(original).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_Json_Without_Figures_Deserializes_As_Null_Figures()
    {
        // Zero-migration (#477): rows persisted before figure retention have no Figures member, so the
        // constructor's optional parameter defaults it to null.
        var legacyJson = "{\"ProviderName\":\"PdfPig\",\"NativePayloadManifest\":null,\"IsComplete\":true}";

        var metadata = JsonSerializer.Deserialize<DocumentParseMetadata>(legacyJson);

        metadata.ShouldNotBeNull();
        metadata!.Figures.ShouldBeNull();
    }
}
