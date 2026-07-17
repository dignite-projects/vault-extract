using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Contract + normalization semantics for <see cref="FieldExtractionWorkflow"/> (#204 typed values, #527 §1/§3
/// value+warning envelope). IChatClient is replaced with NSubstitute — no real LLM. The workflow now returns a
/// <see cref="FieldExtractionWorkflowResult"/> (<c>{ values, validationWarnings }</c>): values keep the strict
/// typed-validation behavior (matching types kept, mismatches nulled, now under the <c>values</c> key), and warnings are
/// defensively normalized (undeclared / blank / malformed discarded, deduped per field, truncated, capped) without ever
/// dropping a valid value. These tests exercise the server-side parser, not the JSON schema (the mock returns raw JSON).
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
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions())));
    }

    private static FieldExtractionDescriptor Field(string name, FieldDataType type)
        => new(System.Guid.NewGuid(), name, $"Extract {name}.", type, false, false);

    // #212: multi-value text field (AllowMultiple).
    private static FieldExtractionDescriptor MultiField(string name)
        => new(System.Guid.NewGuid(), name, $"Extract {name}.", FieldDataType.Text, false, true);

    // ─── values: typed validation (unchanged behavior, now under the `values` key) ───

    [Fact]
    public async Task Keeps_values_matching_declared_type()
    {
        var json = """
        {
          "values": {
            "amount": 1500.50,
            "count": 3,
            "active": true,
            "signed_on": "2024-01-15",
            "party": "Acme Corp"
          },
          "validationWarnings": []
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

        result.Values["amount"]!.Value.GetDecimal().ShouldBe(1500.50m);
        result.Values["count"]!.Value.GetInt64().ShouldBe(3);
        result.Values["active"]!.Value.GetBoolean().ShouldBeTrue();
        result.Values["signed_on"]!.Value.GetString().ShouldBe("2024-01-15");
        result.Values["party"]!.Value.GetString().ShouldBe("Acme Corp");
        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nulls_values_that_do_not_match_declared_type()
    {
        // Dirty values are blocked by strict validation and stored as null, preserving ExtractedFields type consistency.
        var json = """
        {
          "values": {
            "amount": "about 100k",
            "signed_on": "2024/01/15",
            "active": "true",
            "party": 123,
            "count": true
          },
          "validationWarnings": []
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

        result.Values["amount"].ShouldBeNull();
        result.Values["signed_on"].ShouldBeNull();
        result.Values["active"].ShouldBeNull();
        result.Values["party"].ShouldBeNull();
        result.Values["count"].ShouldBeNull();
    }

    [Fact]
    public async Task DateTime_must_be_offset_free_wall_clock()
    {
        var json = """
        {
          "values": {
            "offset_free": "2024-01-01T10:00:00",
            "with_offset": "2024-01-01T10:00:00+08:00",
            "utc_z": "2024-01-01T10:00:00Z"
          },
          "validationWarnings": []
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

        result.Values["offset_free"]!.Value.GetString().ShouldBe("2024-01-01T10:00:00");
        result.Values["with_offset"].ShouldBeNull();
        result.Values["utc_z"].ShouldBeNull();
    }

    [Fact]
    public async Task Missing_or_explicit_null_becomes_null()
    {
        var workflow = CreateWorkflow(
            """{ "values": { "present": "x", "explicit_null": null }, "validationWarnings": [] }""");

        var result = await workflow.ExtractAsync(
            new[]
            {
                Field("present", FieldDataType.Text),
                Field("explicit_null", FieldDataType.Text),
                Field("absent", FieldDataType.Text),
            },
            "# doc");

        result.Values["present"]!.Value.GetString().ShouldBe("x");
        result.Values["explicit_null"].ShouldBeNull();
        result.Values["absent"].ShouldBeNull();
    }

    [Fact]
    public async Task Non_json_output_nulls_all_fields_and_has_no_warnings()
    {
        var workflow = CreateWorkflow("sorry, I can't do that");

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", FieldDataType.Number) },
            "# doc");

        result.Values["amount"].ShouldBeNull();
        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_values_key_nulls_all_fields()
    {
        // Schema drift: no `values` key -> every field is null (graceful degradation, like the old flat-shape miss).
        var workflow = CreateWorkflow("""{ "validationWarnings": [] }""");

        var result = await workflow.ExtractAsync(
            new[] { Field("amount", FieldDataType.Number), Field("party", FieldDataType.Text) },
            "# doc");

        result.Values["amount"].ShouldBeNull();
        result.Values["party"].ShouldBeNull();
    }

    [Fact]
    public async Task Multi_value_string_field_keeps_json_array_and_rejects_non_array()
    {
        var json = """
        {
          "values": {
            "tags": ["urgent", "legal", "2026"],
            "scalar_tags": "urgent",
            "bad_tags": ["ok", 123]
          },
          "validationWarnings": []
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

        result.Values["tags"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
        result.Values["tags"]!.Value.GetArrayLength().ShouldBe(3);
        result.Values["scalar_tags"].ShouldBeNull();   // Multi-value field received a scalar: type mismatch -> null.
        result.Values["bad_tags"].ShouldBeNull();       // Array contains a non-string element -> null.
    }

    [Fact]
    public async Task Multi_value_array_exceeding_count_cap_is_nulled()
    {
        var elements = string.Join(",", Enumerable.Range(0, DocumentExtractedFieldConsts.MaxMultiValueCount + 1)
            .Select(i => $"\"t{i}\""));
        var workflow = CreateWorkflow($$"""{ "values": { "tags": [{{elements}}] }, "validationWarnings": [] }""");

        var result = await workflow.ExtractAsync(new[] { MultiField("tags") }, "# doc");

        result.Values["tags"].ShouldBeNull();
    }

    [Fact]
    public async Task Empty_field_list_short_circuits_without_calling_llm()
    {
        var workflow = CreateWorkflow("{}");

        var result = await workflow.ExtractAsync(Array.Empty<FieldExtractionDescriptor>(), "# doc");

        result.Values.ShouldBeEmpty();
        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Over_budget_schema_assertion_blocks_the_llm_call()
    {
        var chatClient = Substitute.For<IChatClient>();
        var workflow = new FieldExtractionWorkflow(
            chatClient,
            NullLogger<FieldExtractionWorkflow>.Instance,
            new FieldSchemaPromptBudgetGuard(Options.Create(new VaultExtractBehaviorOptions
            {
                MaxFieldSchemaPromptLength = 4
            })));
        var fields = new[]
        {
            new FieldExtractionDescriptor(
                Guid.NewGuid(), "body", "12345", FieldDataType.Text, IsRequired: false, AllowMultiple: false)
        };

        await Should.ThrowAsync<InvalidOperationException>(() => workflow.ExtractAsync(fields, "# doc"));

        await chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── validationWarnings: server-side normalization (#527 §3) ───

    [Fact]
    public async Task Warning_is_returned_and_the_field_value_is_kept()
    {
        // The core #527 contract: a warned field KEEPS its value (never nulled), and the warning is returned separately.
        var json = """
        {
          "values": { "transactions": "| Date | Balance |" },
          "validationWarnings": [
            { "fieldName": "transactions", "message": "Row 4 balance does not reconcile." }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("transactions", FieldDataType.Text) }, "# doc");

        result.Values["transactions"].ShouldNotBeNull();   // value preserved despite the warning
        result.ValidationWarnings.Count.ShouldBe(1);
        result.ValidationWarnings[0].FieldName.ShouldBe("transactions");
        result.ValidationWarnings[0].Message.ShouldBe("Row 4 balance does not reconcile.");
    }

    [Fact]
    public async Task Warning_for_undeclared_field_is_discarded()
    {
        var json = """
        {
          "values": { "amount": 100 },
          "validationWarnings": [
            { "fieldName": "amount", "message": "problem" },
            { "fieldName": "not_a_field", "message": "should be dropped" }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("amount", FieldDataType.Number) }, "# doc");

        result.ValidationWarnings.Select(w => w.FieldName).ShouldBe(new[] { "amount" });
    }

    [Fact]
    public async Task Blank_message_is_discarded()
    {
        var workflow = CreateWorkflow(
            """{ "values": { "amount": 100 }, "validationWarnings": [ { "fieldName": "amount", "message": "   " } ] }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", FieldDataType.Number) }, "# doc");

        result.ValidationWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_warnings_for_one_field_are_merged_to_one()
    {
        var json = """
        {
          "values": { "amount": 100 },
          "validationWarnings": [
            { "fieldName": "amount", "message": "first" },
            { "fieldName": "amount", "message": "second" }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("amount", FieldDataType.Number) }, "# doc");

        result.ValidationWarnings.Count.ShouldBe(1);
        result.ValidationWarnings[0].Message.ShouldBe("first");   // first wins
    }

    [Fact]
    public async Task Overlong_message_is_truncated_at_char_boundary()
    {
        var longMessage = new string('x', DocumentFieldValidationWarningConsts.MaxMessageLength + 50);
        var workflow = CreateWorkflow(
            $$"""{ "values": { "amount": 100 }, "validationWarnings": [ { "fieldName": "amount", "message": "{{longMessage}}" } ] }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", FieldDataType.Number) }, "# doc");

        result.ValidationWarnings.Single().Message.Length
            .ShouldBe(DocumentFieldValidationWarningConsts.MaxMessageLength);
    }

    [Fact]
    public async Task Excess_warnings_are_capped()
    {
        // More distinct warned fields than the cap -> only MaxWarningsPerExtraction warnings are kept.
        var fields = Enumerable.Range(0, DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction + 5)
            .Select(i => Field($"f{i}", FieldDataType.Text)).ToArray();
        var values = string.Join(",", fields.Select(f => $$""" "{{f.Name}}": "v" """));
        var warnings = string.Join(",", fields.Select(f => $$"""{ "fieldName": "{{f.Name}}", "message": "bad" }"""));
        var workflow = CreateWorkflow($$"""{ "values": { {{values}} }, "validationWarnings": [ {{warnings}} ] }""");

        var result = await workflow.ExtractAsync(fields, "# doc");

        result.ValidationWarnings.Count.ShouldBe(DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction);
    }

    [Fact]
    public async Task Malformed_warning_entries_do_not_drop_valid_values_or_the_valid_warning()
    {
        // A non-object entry, a missing-message entry, and a wrong-typed fieldName are all discarded; the valid value and
        // the valid warning survive — a malformed warning never corrupts the values half (#527 §3).
        var json = """
        {
          "values": { "amount": 100 },
          "validationWarnings": [
            "not-an-object",
            { "fieldName": "amount" },
            { "fieldName": 42, "message": "x" },
            { "fieldName": "amount", "message": "valid" }
          ]
        }
        """;
        var workflow = CreateWorkflow(json);

        var result = await workflow.ExtractAsync(new[] { Field("amount", FieldDataType.Number) }, "# doc");

        result.Values["amount"]!.Value.GetInt64().ShouldBe(100);
        result.ValidationWarnings.Count.ShouldBe(1);
        result.ValidationWarnings[0].Message.ShouldBe("valid");
    }

    [Fact]
    public async Task Missing_validationWarnings_key_yields_empty()
    {
        var workflow = CreateWorkflow("""{ "values": { "amount": 100 } }""");

        var result = await workflow.ExtractAsync(new[] { Field("amount", FieldDataType.Number) }, "# doc");

        result.Values["amount"].ShouldNotBeNull();
        result.ValidationWarnings.ShouldBeEmpty();
    }
}
