using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #527 §4/§5/§7: the <see cref="Document"/> aggregate's field validation warning collection, its coupling to the
/// blocking <see cref="DocumentReviewReasons.FieldValidationWarning"/> review bit (the collection and the bit must never
/// diverge), and the §7 rule that every type change clears the warnings immediately. The transition methods are
/// internal, so they are invoked by reflection (mirrors <c>IsSegmentedTransitionMatrix_Tests</c>).
/// </summary>
public class DocumentFieldValidationWarning_Tests
{
    private static readonly Guid FieldA = FieldId("a");
    private static readonly Guid FieldB = FieldId("b");
    private static readonly Guid FieldC = FieldId("c");

    // --- reconcile + bit coupling (§4) ---

    [Fact]
    public void Replace_With_Warnings_Sets_Collection_And_Blocking_Bit()
    {
        var doc = ClassifiedDocument();

        doc.ReplaceFieldValidationWarnings(new[]
        {
            new FieldValidationWarning(FieldA, "Balance after row 4 does not reconcile."),
            new FieldValidationWarning(FieldB, "Closing balance mismatch.")
        });

        doc.FieldValidationWarnings.Count.ShouldBe(2);
        HasWarningBit(doc).ShouldBeTrue();
    }

    [Fact]
    public void Replace_With_Empty_Clears_Collection_And_Bit()
    {
        var doc = WarnedDocument();

        doc.ReplaceFieldValidationWarnings(Array.Empty<FieldValidationWarning>());

        AssertNoWarnings(doc);
    }

    [Fact]
    public void Replace_Null_Clears_Collection_And_Bit()
    {
        var doc = WarnedDocument();

        doc.ReplaceFieldValidationWarnings(null);

        AssertNoWarnings(doc);
    }

    [Fact]
    public void Replace_Reconciles_By_Field_Updating_Adding_And_Removing()
    {
        var doc = ClassifiedDocument();
        doc.ReplaceFieldValidationWarnings(new[]
        {
            new FieldValidationWarning(FieldA, "old A"),
            new FieldValidationWarning(FieldB, "B stays until dropped")
        });

        doc.ReplaceFieldValidationWarnings(new[]
        {
            new FieldValidationWarning(FieldA, "new A"),   // updated in place
            new FieldValidationWarning(FieldC, "C added")  // inserted; B dropped
        });

        var byField = doc.FieldValidationWarnings.ToDictionary(w => w.FieldDefinitionId, w => w.Message);
        byField.Count.ShouldBe(2);
        byField[FieldA].ShouldBe("new A");
        byField[FieldC].ShouldBe("C added");
        byField.ShouldNotContainKey(FieldB);
        HasWarningBit(doc).ShouldBeTrue();
    }

    // --- blocking policy (§5) ---

    [Fact]
    public void FieldValidationWarning_Is_Blocking_In_Policy()
    {
        ReviewReasonPolicy.IsBlocking(DocumentReviewReasons.FieldValidationWarning).ShouldBeTrue();
        ReviewReasonPolicy.HasBlocking(DocumentReviewReasons.FieldValidationWarning).ShouldBeTrue();
    }

    // --- §7 clearing on every type change ---

    [Fact]
    public void Reclassify_Automatic_Clears_Warnings()
    {
        var doc = WarnedDocument();
        ApplyAutomaticClassificationResult(doc, TypeId("invoice.general"), 0.95);
        AssertNoWarnings(doc);
    }

    [Fact]
    public void Reclassify_Confirm_Clears_Warnings()
    {
        var doc = WarnedDocument();
        ConfirmClassification(doc, TypeId("invoice.general"));
        AssertNoWarnings(doc);
    }

    [Fact]
    public void Becoming_Unclassified_Clears_Warnings()
    {
        var doc = WarnedDocument();
        RequestClassificationReview(doc);
        AssertNoWarnings(doc);
    }

    [Fact]
    public void Becoming_Container_Clears_Warnings()
    {
        var doc = WarnedDocument();
        MarkAsContainer(doc);
        AssertNoWarnings(doc);
    }

    // --- helpers ---

    private static void AssertNoWarnings(Document doc)
    {
        doc.FieldValidationWarnings.ShouldBeEmpty();
        HasWarningBit(doc).ShouldBeFalse();
    }

    private static bool HasWarningBit(Document doc)
        => (doc.ReviewReasons & DocumentReviewReasons.FieldValidationWarning) == DocumentReviewReasons.FieldValidationWarning;

    private static Document ClassifiedDocument()
    {
        var doc = NewDocument();
        ApplyAutomaticClassificationResult(doc, TypeId("contract.general"), 0.95);
        return doc;
    }

    private static Document WarnedDocument()
    {
        var doc = ClassifiedDocument();
        doc.ReplaceFieldValidationWarnings(new[] { new FieldValidationWarning(FieldA, "mismatch") });
        HasWarningBit(doc).ShouldBeTrue();
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

    private static void ApplyAutomaticClassificationResult(Document doc, Guid typeId, double confidence)
        => Invoke(doc, "ApplyAutomaticClassificationResult", typeId, confidence);

    private static void ConfirmClassification(Document doc, Guid typeId)
        => Invoke(doc, "ConfirmClassification", typeId);

    private static void RequestClassificationReview(Document doc)
        => Invoke(doc, "RequestClassificationReview");

    private static void MarkAsContainer(Document doc)
        => Invoke(doc, "MarkAsContainer");

    private static void Invoke(Document doc, string method, params object[] args)
        => typeof(Document)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, args);

    private static Guid TypeId(string typeCode) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}
