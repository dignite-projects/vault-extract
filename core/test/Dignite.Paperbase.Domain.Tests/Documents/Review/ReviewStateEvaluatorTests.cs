using System;
using Dignite.Paperbase.Documents.Review;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// #284：审核判定纯函数单测——<see cref="ReviewStateEvaluator"/>（必填缺失维度）+
/// <see cref="ReviewReasonPolicy"/>（阻断性）。不依赖 DB / DI，直接构造。
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

    // 只有 UnresolvedClassification 是 blocking（阻断 Ready）；MissingRequiredFields 是 non-blocking。
    [Theory]
    [InlineData(DocumentReviewReasons.None, false)]
    [InlineData(DocumentReviewReasons.UnresolvedClassification, true)]
    [InlineData(DocumentReviewReasons.MissingRequiredFields, false)]
    [InlineData(DocumentReviewReasons.UnresolvedClassification | DocumentReviewReasons.MissingRequiredFields, true)]
    public void HasBlocking_Only_UnresolvedClassification_Is_Blocking(DocumentReviewReasons reasons, bool expected)
    {
        ReviewReasonPolicy.HasBlocking(reasons).ShouldBe(expected);
    }

    // #284 review-fix：操作员"需关注"统一判据 = 有未解决原因 且 未被拒绝（Rejected 抑制需关注——操作员已处置）。
    // 钉死四象限 + (MissingRequiredFields, Confirmed)→true：防止把判据误写成 disposition==NotReviewed。
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
