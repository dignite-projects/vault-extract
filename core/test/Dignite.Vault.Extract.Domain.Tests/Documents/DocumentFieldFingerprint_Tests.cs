using System;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #651 §6: <see cref="Document.SetFieldFingerprint"/> and the rule folded into it — a <b>changed</b> key
/// withdraws the operator's <see cref="Document.DuplicateAllowed"/> verdict, because that verdict was about the
/// key values that were there when they made it.
/// <para>
/// The rule lives on the aggregate rather than at the four call sites that write a fingerprint (the extraction
/// write, extraction's no-definitions clear, the #528 duplicate-basis cleanup and the operator's manual
/// correction). Sorting an invariant into methods leaks the moment a fifth write site appears; these tests pin
/// it at the transition instead, which is the only place all five have to pass through.
/// </para>
/// </summary>
public class DocumentFieldFingerprint_Tests
{
    private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void A_Changed_Key_Withdraws_The_Operators_Not_A_Duplicate_Verdict()
    {
        var document = AllowedDocumentWith(KeyA);

        document.SetFieldFingerprint(KeyB).ShouldBeTrue();

        document.FieldFingerprint.ShouldBe(KeyB);
        document.DuplicateAllowed.ShouldBeFalse();
    }

    [Fact]
    public void An_Unchanged_Key_Keeps_The_Verdict_So_ReExtraction_Does_Not_Undo_The_Operator()
    {
        // Routine re-extraction reproduces the same key. #411's whole point is that the override survives it.
        var document = AllowedDocumentWith(KeyA);

        document.SetFieldFingerprint(KeyA).ShouldBeFalse();

        document.FieldFingerprint.ShouldBe(KeyA);
        document.DuplicateAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Null_To_Null_Is_Not_A_Change()
    {
        var document = CreateDocument();
        document.AllowDuplicate();

        document.SetFieldFingerprint(null).ShouldBeFalse();

        document.FieldFingerprint.ShouldBeNull();
        document.DuplicateAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Losing_The_Key_Entirely_Withdraws_The_Verdict()
    {
        // The #528 duplicate-basis cleanup and extraction's no-definitions branch both write null when the type
        // has no unique key left. A verdict about a key that no longer exists applies to nothing, the same
        // reasoning ResetDuplicateDetectionState applies on a type change.
        var document = AllowedDocumentWith(KeyA);

        document.SetFieldFingerprint(null).ShouldBeTrue();

        document.FieldFingerprint.ShouldBeNull();
        document.DuplicateAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Gaining_A_Key_Withdraws_The_Verdict_Too()
    {
        var document = CreateDocument();
        document.AllowDuplicate();

        document.SetFieldFingerprint(KeyA).ShouldBeTrue();

        document.FieldFingerprint.ShouldBe(KeyA);
        document.DuplicateAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Whitespace_Normalizes_To_Null_Before_The_Comparison()
    {
        // "   " and null are the same absence, so writing one over the other must not read as a change and must
        // not withdraw anything.
        var document = CreateDocument();
        document.AllowDuplicate();

        document.SetFieldFingerprint("   ").ShouldBeFalse();

        document.FieldFingerprint.ShouldBeNull();
        document.DuplicateAllowed.ShouldBeTrue();
    }

    [Fact]
    public void The_Review_Reason_Is_Left_To_The_Caller()
    {
        // The caller recomputes the verdict in the same operation, so clearing the reason here would fight it.
        var document = AllowedDocumentWith(KeyA);
        document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);

        document.SetFieldFingerprint(KeyB);

        (document.ReviewReasons & DocumentReviewReasons.DuplicateSuspected)
            .ShouldBe(DocumentReviewReasons.DuplicateSuspected);
    }

    private static Document AllowedDocumentWith(string fingerprint)
    {
        var document = CreateDocument();
        document.SetFieldFingerprint(fingerprint);
        document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
        // Through the aggregate, so the starting state is exactly what an operator override leaves behind.
        document.AllowDuplicate();
        document.DuplicateAllowed.ShouldBeTrue();
        return document;
    }

    private static Document CreateDocument() =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: "blobs/test.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
}
