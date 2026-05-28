using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// <see cref="FieldExtractionWorkflow"/> 的归一化 / 强校验语义（Issue #204 任务 2）。
/// IChatClient 用 NSubstitute 替代（无真实 LLM）；只验证「LLM 输出按声明 <see cref="FieldDataType"/>
/// 校验——合类型保留、不合类型存 null」这套自有逻辑（与操作员手改路径共用 ExtractedFieldValueValidator）。
/// </summary>
public class FieldExtractionWorkflow_Tests
{
    private static FieldExtractionWorkflow CreateWorkflow(string jsonResponse)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)])));

        return new FieldExtractionWorkflow(
            chatClient,
            Options.Create(new PaperbaseAIBehaviorOptions()),
            NullLogger<FieldExtractionWorkflow>.Instance);
    }

    private static FieldExtractionDescriptor Field(string name, FieldDataType type)
        => new(System.Guid.NewGuid(), name, $"Extract {name}.", type, false);

    [Fact]
    public async Task Keeps_values_matching_declared_type()
    {
        var json = """
        {
          "amount": 1500.50,
          "count": 3,
          "active": true,
          "signed_on": "2024-01-15",
          "party": "Acme Corp"
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("amount", FieldDataType.Number),
                Field("count", FieldDataType.Number),
                Field("active", FieldDataType.Boolean),
                Field("signed_on", FieldDataType.Date),
                Field("party", FieldDataType.String),
            },
            "# doc");

        result["amount"]!.Value.GetDecimal().ShouldBe(1500.50m);
        result["count"]!.Value.GetInt64().ShouldBe(3);
        result["active"]!.Value.GetBoolean().ShouldBeTrue();
        result["signed_on"]!.Value.GetString().ShouldBe("2024-01-15");
        result["party"]!.Value.GetString().ShouldBe("Acme Corp");
    }

    [Fact]
    public async Task Nulls_values_that_do_not_match_declared_type()
    {
        // 脏值：货币串给 Number、非 ISO 日期给 Date、字符串 "true" 给 Boolean、数字给 String、布尔给 Number。
        // 全部应被强校验拦下存 null——保证 ExtractedFields 类型自洽（任务 3 类型化查询的干净数据前提）。
        var json = """
        {
          "amount": "约10万",
          "signed_on": "2024/01/15",
          "active": "true",
          "party": 123,
          "count": true
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("amount", FieldDataType.Number),
                Field("signed_on", FieldDataType.Date),
                Field("active", FieldDataType.Boolean),
                Field("party", FieldDataType.String),
                Field("count", FieldDataType.Number),
            },
            "# doc");

        result["amount"].ShouldBeNull();
        result["signed_on"].ShouldBeNull();
        result["active"].ShouldBeNull();
        result["party"].ShouldBeNull();
        result["count"].ShouldBeNull();
    }

    [Fact]
    public async Task DateTime_must_be_offset_free_wall_clock()
    {
        // 通道 DateTime 字段统一为无偏移 wall-clock——带偏移 / Z 的值会让 datetime2 列比较随服务器时区
        // 漂移，故抽取阶段就拦下存 null（Codex 评审 finding 2）。
        var json = """
        {
          "offset_free": "2024-01-01T10:00:00",
          "with_offset": "2024-01-01T10:00:00+08:00",
          "utc_z": "2024-01-01T10:00:00Z"
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("offset_free", FieldDataType.DateTime),
                Field("with_offset", FieldDataType.DateTime),
                Field("utc_z", FieldDataType.DateTime),
            },
            "# doc");

        result["offset_free"]!.Value.GetString().ShouldBe("2024-01-01T10:00:00");
        result["with_offset"].ShouldBeNull();
        result["utc_z"].ShouldBeNull();
    }

    [Fact]
    public async Task Missing_or_explicit_null_becomes_null()
    {
        var workflow = CreateWorkflow("""{ "present": "x", "explicit_null": null }""");

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("present", FieldDataType.String),
                Field("explicit_null", FieldDataType.String),
                Field("absent", FieldDataType.String),
            },
            "# doc");

        result["present"]!.Value.GetString().ShouldBe("x");
        result["explicit_null"].ShouldBeNull();
        result["absent"].ShouldBeNull();
    }

    [Fact]
    public async Task Non_json_output_nulls_all_fields()
    {
        var workflow = CreateWorkflow("sorry, I can't do that");

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", FieldDataType.Number) },
            "# doc");

        result["amount"].ShouldBeNull();
    }

    [Fact]
    public async Task Empty_field_list_short_circuits_without_calling_llm()
    {
        var workflow = CreateWorkflow("{}");

        var result = await workflow.ExtractAsync(Array.Empty<FieldExtractionDescriptor>(), "# doc");

        result.ShouldBeEmpty();
    }
}
