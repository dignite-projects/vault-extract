using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Regression protection: DocumentClassificationWorkflow's two-layer defense for LLM confidence.
///
/// Background: LLMs occasionally return percentage confidence, such as 99.9, or NaN / &lt;0 / &gt;100 confidence.
/// Passing it through to Document.ApplyAutomaticClassificationResult would trigger aggregate-root
/// Check.Range(0,1), throwing ArgumentException and turning the whole PipelineRun to Failed.
/// Fix strategy:
///   - top-level percentage confidence -> normalize to 0..1.
///   - top-level truly invalid confidence -> treat as "no reliable conclusion" (typeCode=null + confidence=0),
///     so BackgroundJob takes the LowConfidence path and triggers PendingReview.
///   - out-of-range candidate confidence -> clamp to [0,1]; candidates are only for UI / Run persistence and do
///     not affect aggregate-root invariants.
///
/// These two judgment functions are all the load-bearing logic for the fix; the branch statements around them
/// in RunAsync only assign values and are visually correct.
/// </summary>
public class DocumentClassificationConfidenceGuardTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void IsValidConfidence_Returns_True_For_Values_In_Range(double value)
    {
        DocumentClassificationWorkflow.IsValidConfidence(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    [InlineData(1.5)]
    [InlineData(-100)]
    [InlineData(100)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void IsValidConfidence_Returns_False_For_Out_Of_Range_Or_Invalid(double value)
    {
        DocumentClassificationWorkflow.IsValidConfidence(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0.92, 0.92)]
    [InlineData(92, 0.92)]
    [InlineData(99.9, 0.999)]
    [InlineData(100, 1.0)]
    public void TryNormalizeConfidence_Accepts_Decimal_And_Percentage_Values(double input, double expected)
    {
        DocumentClassificationWorkflow.TryNormalizeConfidence(input, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected, 0.000001);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TryNormalizeConfidence_Rejects_Invalid_Values(double input)
    {
        DocumentClassificationWorkflow.TryNormalizeConfidence(input, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    public void ClampConfidence_Preserves_In_Range_Values(double input, double expected)
    {
        DocumentClassificationWorkflow.ClampConfidence(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(-1.0)]
    [InlineData(-100)]
    [InlineData(double.NegativeInfinity)]
    public void ClampConfidence_Returns_Zero_For_Below_Range(double input)
    {
        DocumentClassificationWorkflow.ClampConfidence(input).ShouldBe(0d);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(100)]
    [InlineData(double.PositiveInfinity)]
    public void ClampConfidence_Returns_One_For_Above_Range(double input)
    {
        DocumentClassificationWorkflow.ClampConfidence(input).ShouldBe(1d);
    }

    [Fact]
    public void ClampConfidence_Returns_Zero_For_NaN()
    {
        DocumentClassificationWorkflow.ClampConfidence(double.NaN).ShouldBe(0d);
    }
}
