using System;
using System.Collections.Generic;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Cabinets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Pure parsing / boundary unit tests for <see cref="CabinetSuggestionWorkflow"/> with no DI and no real
/// LLM. Verifies 1-based number to candidate <see cref="Cabinet.Id"/> mapping, abstain cases
/// (null / 0 / out-of-range), confidence clamp, and truncation boundary.
/// </summary>
public class CabinetSuggestionWorkflow_Tests
{
    private static CabinetSuggestionWorkflow CreateWorkflow(ExtractBehaviorOptions? options = null)
        => new(
            Substitute.For<IChatClient>(),
            Options.Create(options ?? new ExtractBehaviorOptions()));

    private static List<Cabinet> Candidates(params string[] names)
    {
        var list = new List<Cabinet>(names.Length);
        foreach (var name in names)
        {
            list.Add(new Cabinet(Guid.NewGuid(), tenantId: null, name));
        }
        return list;
    }

    [Fact]
    public void Maps_OneBased_Index_To_Candidate_Id()
    {
        var workflow = CreateWorkflow();
        var candidates = Candidates("法务", "财务", "人事");

        var outcome = workflow.ResolveOutcome(
            new CabinetSuggestionWorkflow.CabinetSuggestionResponse { CabinetIndex = 2, Confidence = 0.9 },
            candidates);

        outcome.CabinetId.ShouldBe(candidates[1].Id);
        outcome.Confidence.ShouldBe(0.9);
    }

    [Fact]
    public void Selects_First_And_Last_Candidate_At_Boundaries()
    {
        var workflow = CreateWorkflow();
        var candidates = Candidates("A", "B", "C");

        workflow.ResolveOutcome(
            new CabinetSuggestionWorkflow.CabinetSuggestionResponse { CabinetIndex = 1, Confidence = 0.8 },
            candidates).CabinetId.ShouldBe(candidates[0].Id);

        workflow.ResolveOutcome(
            new CabinetSuggestionWorkflow.CabinetSuggestionResponse { CabinetIndex = 3, Confidence = 0.8 },
            candidates).CabinetId.ShouldBe(candidates[2].Id);
    }

    [Fact]
    public void Abstains_When_Index_Is_Null()
    {
        var workflow = CreateWorkflow();

        var outcome = workflow.ResolveOutcome(
            new CabinetSuggestionWorkflow.CabinetSuggestionResponse { CabinetIndex = null, Confidence = 0.2 },
            Candidates("A", "B"));

        outcome.CabinetId.ShouldBeNull();
    }

    [Fact]
    public void Abstains_When_Response_Is_Null()
    {
        var workflow = CreateWorkflow();

        var outcome = workflow.ResolveOutcome(null, Candidates("A", "B"));

        outcome.CabinetId.ShouldBeNull();
        outcome.Confidence.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]    // LLM returned 0, not 1-based.
    [InlineData(-1)]   // Negative number.
    [InlineData(3)]    // Exceeds candidate count; there are 2 candidates.
    [InlineData(99)]
    public void Abstains_When_Index_Out_Of_Range(int index)
    {
        var workflow = CreateWorkflow();

        var outcome = workflow.ResolveOutcome(
            new CabinetSuggestionWorkflow.CabinetSuggestionResponse { CabinetIndex = index, Confidence = 0.95 },
            Candidates("A", "B"));

        outcome.CabinetId.ShouldBeNull();
    }

    [Fact]
    public void Clamps_Out_Of_Range_Confidence()
    {
        CabinetSuggestionWorkflow.ClampConfidence(1.5).ShouldBe(1.0);
        CabinetSuggestionWorkflow.ClampConfidence(-0.2).ShouldBe(0.0);
        CabinetSuggestionWorkflow.ClampConfidence(double.NaN).ShouldBe(0.0);
        CabinetSuggestionWorkflow.ClampConfidence(0.42).ShouldBe(0.42);
    }

    [Fact]
    public void Truncate_Does_Not_Split_Surrogate_Pair()
    {
        // "A" + 😀 (surrogate pair) + "B"; maxChars=2 lands inside the surrogate pair, so the entire high
        // surrogate is dropped and the result is "A".
        var result = CabinetSuggestionWorkflow.TruncateAtCharBoundary("A\U0001F600B", 2);

        result.ShouldBe("A");
        char.IsHighSurrogate(result[^1]).ShouldBeFalse();
    }

    [Fact]
    public void Truncate_Returns_Text_Unchanged_When_Within_Limit()
    {
        CabinetSuggestionWorkflow.TruncateAtCharBoundary("hello", 10).ShouldBe("hello");
    }

    [Fact]
    public void FormatCandidates_Numbers_Are_One_Based()
    {
        var result = CabinetSuggestionWorkflow.FormatCandidates(Candidates("法务", "财务"));

        result.ShouldContain("1. ");
        result.ShouldContain("2. ");
    }

    [Fact]
    public void FormatCandidates_Wraps_Name_With_PromptBoundary()
    {
        var result = CabinetSuggestionWorkflow.FormatCandidates(Candidates("法务"));

        result.ShouldContain(PromptBoundary.WrapField("法务")!);
    }

    [Fact]
    public void FormatCandidates_Appends_Description_When_Present()
    {
        var candidates = new List<Cabinet>
        {
            new(Guid.NewGuid(), null, "法务", "存放对外合同"),
        };

        var result = CabinetSuggestionWorkflow.FormatCandidates(candidates);

        result.ShouldContain(PromptBoundary.WrapField("存放对外合同")!);
        result.ShouldContain(" — ");
    }

    [Fact]
    public void FormatCandidates_Omits_Description_When_Absent()
    {
        // Empty description, because the Candidates helper does not set one, emits only the name and does
        // not append " - description".
        var result = CabinetSuggestionWorkflow.FormatCandidates(Candidates("法务"));

        result.ShouldNotContain(" — ");
    }
}
