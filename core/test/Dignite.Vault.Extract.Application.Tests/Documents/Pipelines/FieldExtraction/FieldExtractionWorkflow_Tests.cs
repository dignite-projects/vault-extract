using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Normalization / strict-validation semantics for <see cref="FieldExtractionWorkflow"/> (Issue #204 task 2).
/// IChatClient is replaced with NSubstitute, with no real LLM. These tests verify only service-owned logic:
/// LLM output is validated according to the declared <see cref="FieldDataType"/>. Matching types are kept;
/// mismatches are stored as null. This shares ExtractedFieldValueValidator with the operator manual-edit path.
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
            NullLogger<FieldExtractionWorkflow>.Instance);
    }

    private static FieldExtractionDescriptor Field(string name, FieldDataType type)
        => new(System.Guid.NewGuid(), name, $"Extract {name}.", type, false, false);

    // #212: multi-value text field (AllowMultiple).
    private static FieldExtractionDescriptor MultiField(string name)
        => new(System.Guid.NewGuid(), name, $"Extract {name}.", FieldDataType.Text, false, true);

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
                Field("party", FieldDataType.Text),
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
        // Dirty values: currency string for Number, non-ISO date for Date, string "true" for Boolean,
        // number for Text, and boolean for Number. All should be blocked by strict validation and stored as null,
        // preserving ExtractedFields type consistency for task 3 typed-query clean-data assumptions.
        var json = """
        {
          "amount": "about 100k",
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
                Field("party", FieldDataType.Text),
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
        // Channel DateTime fields are offset-free wall-clock values. Offset / Z values make datetime2 column
        // comparisons drift with server time zones, so extraction blocks them and stores null (Codex review finding 2).
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
                Field("present", FieldDataType.Text),
                Field("explicit_null", FieldDataType.Text),
                Field("absent", FieldDataType.Text),
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
    public async Task Multi_value_string_field_keeps_json_array_and_rejects_non_array()
    {
        // #212: multi-value text fields keep JSON arrays when every element is a valid string. Scalars and arrays
        // with non-string elements are treated as type mismatches and stored as null.
        var json = """
        {
          "tags": ["urgent", "legal", "2026"],
          "scalar_tags": "urgent",
          "bad_tags": ["ok", 123]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(
            new[]
            {
                MultiField("tags"),
                MultiField("scalar_tags"),
                MultiField("bad_tags"),
            },
            "# doc");

        result["tags"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
        result["tags"]!.Value.GetArrayLength().ShouldBe(3);
        result["scalar_tags"].ShouldBeNull();   // Multi-value field received a scalar: type mismatch -> null.
        result["bad_tags"].ShouldBeNull();       // Array contains a non-string element -> null.
    }

    [Fact]
    public async Task Multi_value_array_exceeding_count_cap_is_nulled()
    {
        // #212: arrays exceeding MaxMultiValueCount are rejected as a whole and stored as null, a hard guardrail
        // against injection-induced row growth.
        var elements = string.Join(",", Enumerable.Range(0, DocumentExtractedFieldConsts.MaxMultiValueCount + 1)
            .Select(i => $"\"t{i}\""));
        var workflow = CreateWorkflow($$"""{ "tags": [{{elements}}] }""");

        var result = await workflow.ExtractAsync(new[] { MultiField("tags") }, "# doc");

        result["tags"].ShouldBeNull();
    }

    [Fact]
    public async Task Empty_field_list_short_circuits_without_calling_llm()
    {
        var workflow = CreateWorkflow("{}");

        var result = await workflow.ExtractAsync(Array.Empty<FieldExtractionDescriptor>(), "# doc");

        result.ShouldBeEmpty();
    }
}
