using System;
using Dignite.Vault.Extract.Documents.Review;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #284: pure-function unit tests for review judgment: <see cref="ReviewStateEvaluator"/> for the missing
/// required-fields dimension plus <see cref="ReviewReasonPolicy"/> for blocking behavior. No DB / DI;
/// constructed directly.
/// </summary>
public class ReviewStateEvaluatorTests
{
    private readonly ReviewStateEvaluator _evaluator = new();

    [Fact]
    public void MissingRequiredFields_Empty_Required_Returns_False()
    {
        _evaluator.MissingRequiredFieldsPresent(Array.Empty<Guid>(), new[] { Guid.NewGuid() })
            .ShouldBeFalse();
    }

    [Fact]
    public void MissingRequiredFields_All_Present_Returns_False()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _evaluator.MissingRequiredFieldsPresent(new[] { a, b }, new[] { a, b, Guid.NewGuid() })
            .ShouldBeFalse();
    }

    [Fact]
    public void MissingRequiredFields_Some_Missing_Returns_True()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _evaluator.MissingRequiredFieldsPresent(new[] { a, b }, new[] { a })
            .ShouldBeTrue();
    }

    [Fact]
    public void MissingRequiredFields_Nothing_Extracted_Returns_True()
    {
        _evaluator.MissingRequiredFieldsPresent(new[] { Guid.NewGuid() }, Array.Empty<Guid>())
            .ShouldBeTrue();
    }

    // Three reasons withhold Ready: UnresolvedClassification (no confirmed type), DuplicateSuspected (#411), and
    // FieldExtractionIncomplete (#491 — fields were expected but the body was too large to extract, so an empty
    // field set would be indistinguishable from a type that declares no fields). MissingRequiredFields and
    // SegmentationIncomplete are non-blocking: the document is still consumable downstream.
    [Theory]
    [InlineData(DocumentReviewReasons.None, false)]
    [InlineData(DocumentReviewReasons.UnresolvedClassification, true)]
    [InlineData(DocumentReviewReasons.DuplicateSuspected, true)]
    [InlineData(DocumentReviewReasons.FieldExtractionIncomplete, true)]
    [InlineData(DocumentReviewReasons.MissingRequiredFields, false)]
    [InlineData(DocumentReviewReasons.SegmentationIncomplete, false)]
    [InlineData(DocumentReviewReasons.MissingRequiredFields | DocumentReviewReasons.SegmentationIncomplete, false)]
    [InlineData(DocumentReviewReasons.UnresolvedClassification | DocumentReviewReasons.MissingRequiredFields, true)]
    [InlineData(DocumentReviewReasons.FieldExtractionIncomplete | DocumentReviewReasons.SegmentationIncomplete, true)]
    public void HasBlocking_Is_True_Exactly_For_The_Blocking_Reasons(DocumentReviewReasons reasons, bool expected)
    {
        ReviewReasonPolicy.HasBlocking(reasons).ShouldBe(expected);
    }

    // #284 review-fix: unified operator "requires attention" rule = has unresolved reason and is not
    // rejected. Rejected suppresses attention because the operator has already handled it.
    // Lock down the four quadrants plus (MissingRequiredFields, Confirmed) -> true, preventing the rule
    // from being mistakenly written as disposition==NotReviewed.
    [Theory]
    [InlineData(DocumentReviewReasons.None, DocumentReviewDisposition.NotReviewed, false)]
    [InlineData(DocumentReviewReasons.None, DocumentReviewDisposition.Confirmed, false)]
    [InlineData(DocumentReviewReasons.None, DocumentReviewDisposition.Rejected, false)]
    [InlineData(DocumentReviewReasons.UnresolvedClassification, DocumentReviewDisposition.NotReviewed, true)]
    [InlineData(DocumentReviewReasons.MissingRequiredFields, DocumentReviewDisposition.NotReviewed, true)]
    [InlineData(DocumentReviewReasons.MissingRequiredFields, DocumentReviewDisposition.Confirmed, true)]
    [InlineData(DocumentReviewReasons.UnresolvedClassification, DocumentReviewDisposition.Rejected, false)]
    [InlineData(DocumentReviewReasons.MissingRequiredFields, DocumentReviewDisposition.Rejected, false)]
    public void RequiresAttention_True_When_Has_Reason_And_Not_Rejected(
        DocumentReviewReasons reasons, DocumentReviewDisposition disposition, bool expected)
    {
        ReviewReasonPolicy.RequiresAttention(reasons, disposition).ShouldBe(expected);
    }
}
