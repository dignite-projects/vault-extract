using System;
using System.Collections.Generic;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents.Cabinets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// <see cref="CabinetSuggestionWorkflow"/> 的纯解析 / 边界单元测试（无 DI、无真实 LLM）——
/// 验证 1-based 编号 → 候选 <see cref="Cabinet.Id"/> 映射、弃选（null / 0 / 越界）、置信度 clamp、截断边界。
/// </summary>
public class CabinetSuggestionWorkflow_Tests
{
    private static CabinetSuggestionWorkflow CreateWorkflow(PaperbaseAIBehaviorOptions? options = null)
        => new(
            Substitute.For<IChatClient>(),
            Options.Create(options ?? new PaperbaseAIBehaviorOptions()));

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
    [InlineData(0)]    // LLM 返回 0（非 1-based）
    [InlineData(-1)]   // 负数
    [InlineData(3)]    // 超过候选数（2 个候选）
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
        // "A" + 😀 (代理对) + "B"；maxChars=2 落在代理对中间 → 丢弃整个高位代理，得 "A"。
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
        // 空描述（Candidates helper 不设描述）→ 只给名称，不追加 " — 描述"。
        var result = CabinetSuggestionWorkflow.FormatCandidates(Candidates("法务"));

        result.ShouldNotContain(" — ");
    }
}
