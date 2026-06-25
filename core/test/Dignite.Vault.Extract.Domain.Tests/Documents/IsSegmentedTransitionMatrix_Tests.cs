using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #379 (MEDIUM) aggregate-level transition matrix for the <see cref="Document.IsContainer"/> ↔
/// <see cref="Document.IsSegmented"/> invariant. <c>IsSegmented</c> is the unified-segmentation resume gate (#377): it
/// MUST be cleared on every container↔concrete reclassification path so a now-concrete document's embedded-document
/// routing is not skipped, and a re-recognized container runs its split exactly once; and it MUST be preserved on a
/// non-transition (the completed work is real, not stale). <c>#378</c> was a silent-data-loss bug where the AUTOMATIC
/// concrete-assigning path forgot to clear it while the operator path cleared it — exactly the divergence this matrix
/// pins down. All three entry points (<c>ApplyAutomaticClassificationResult</c> / <c>ConfirmClassification</c> /
/// <c>MarkAsContainer</c>) now funnel through one <c>SetContainerFlag</c> choke point; these tests assert the
/// observable cleared-on-transition / preserved-on-no-op behaviour for each, independent of that implementation detail.
/// </summary>
public class IsSegmentedTransitionMatrix_Tests
{
    // --- container -> concrete: every reclassification path clears the stale resume marker ---

    [Fact]
    public void Automatic_HighConfidence_Reclassify_Container_To_Concrete_Clears_IsSegmented()
    {
        var doc = SegmentedContainer();

        ApplyAutomaticClassificationResult(doc, TypeId("contract.general"), 0.95);

        doc.IsContainer.ShouldBeFalse();
        doc.IsSegmented.ShouldBeFalse();
    }

    [Fact]
    public void Operator_Confirm_Container_To_Concrete_Clears_IsSegmented()
    {
        var doc = SegmentedContainer();

        ConfirmClassification(doc, TypeId("contract.general"));

        doc.IsContainer.ShouldBeFalse();
        doc.IsSegmented.ShouldBeFalse();
    }

    // --- concrete -> container: clears the marker so the new container runs its split exactly once ---

    [Fact]
    public void Reclassify_Concrete_To_Container_Clears_IsSegmented()
    {
        var doc = SegmentedConcrete(TypeId("contract.general"));

        MarkAsContainer(doc);

        doc.IsContainer.ShouldBeTrue();
        doc.IsSegmented.ShouldBeFalse();
    }

    // --- no transition (flag unchanged): the completion is real work and is preserved, never re-paid ---

    [Fact]
    public void Automatic_Reclassify_Concrete_To_Concrete_Preserves_IsSegmented()
    {
        // A concrete document that already ran its embedded-figure routing (IsSegmented set), re-recognized to another
        // concrete type, keeps the marker: the figure was already routed (the idempotent insert would skip it), so
        // re-running the LLM split would only re-pay for nothing. Not a container transition -> no clear.
        var doc = SegmentedConcrete(TypeId("contract.general"));

        ApplyAutomaticClassificationResult(doc, TypeId("invoice.general"), 0.95);

        doc.IsContainer.ShouldBeFalse();
        doc.IsSegmented.ShouldBeTrue();
    }

    [Fact]
    public void Operator_Confirm_Concrete_To_Concrete_Preserves_IsSegmented()
    {
        var doc = SegmentedConcrete(TypeId("contract.general"));

        ConfirmClassification(doc, TypeId("invoice.general"));

        doc.IsContainer.ShouldBeFalse();
        doc.IsSegmented.ShouldBeTrue();
    }

    [Fact]
    public void Re_MarkAsContainer_On_An_Already_Segmented_Container_Preserves_IsSegmented()
    {
        // A container that STAYS a container (re-detected) keeps its split marker so the split is not re-run; only a
        // false->true transition is newly a container.
        var doc = SegmentedContainer();

        MarkAsContainer(doc);

        doc.IsContainer.ShouldBeTrue();
        doc.IsSegmented.ShouldBeTrue();
    }

    // --- builders + reflection helpers (mirror ContainerMarkerClearedEvent_Tests; the transition methods are internal) ---

    private static Document SegmentedContainer()
    {
        var doc = NewDocument();
        MarkAsContainer(doc);
        doc.MarkSegmented(); // a prior container split completed atomically with the rows (#377)
        doc.IsContainer.ShouldBeTrue();
        doc.IsSegmented.ShouldBeTrue();
        return doc;
    }

    private static Document SegmentedConcrete(Guid typeId)
    {
        var doc = NewDocument();
        ApplyAutomaticClassificationResult(doc, typeId, 0.95); // a concrete type, never a container
        doc.MarkSegmented();                                   // its embedded-figure routing completed
        doc.IsContainer.ShouldBeFalse();
        doc.IsSegmented.ShouldBeTrue();
        return doc;
    }

    private static Document NewDocument()
        => new(
            Guid.NewGuid(), null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

    private static void MarkAsContainer(Document doc)
        => typeof(Document)
            .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, null);

    private static void ApplyAutomaticClassificationResult(Document doc, Guid typeId, double confidence)
        => typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, [typeId, confidence]);

    private static void ConfirmClassification(Document doc, Guid typeId)
        => typeof(Document)
            .GetMethod("ConfirmClassification", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, [typeId]);

    private static Guid TypeId(string typeCode)
        => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
}
