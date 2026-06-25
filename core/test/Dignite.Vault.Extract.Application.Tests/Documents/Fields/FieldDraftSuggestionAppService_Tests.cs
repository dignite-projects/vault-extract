using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Parse / sanitize / guardrail / fail-open semantics for FieldDraftSuggestionAppService (issue #264).
/// IChatClient is replaced with NSubstitute and no real LLM call is made. Permission assertions
/// (<c>CheckDraftPermissionAsync</c>) are allowed by the test subclass because the unit is created directly
/// with no HTTP authorization context. These tests verify only the service-owned output handling logic.
/// </summary>
public class FieldDraftSuggestionAppService_Tests
{
    // Test subclass: allow permission assertions. The production path is protected by class-level [Authorize]
    // plus the Create || Update checks in CheckDraftPermissionAsync.
    // [DisableConventionalRegistration]: prevents ABP from auto-registering this derived ApplicationService
    // into DI. Otherwise full CI test-host construction enables Castle DynamicProxy class interceptors for this
    // private test double, fails because it is inaccessible, and brings down the whole test host.
    [DisableConventionalRegistration]
    private sealed class TestableFieldDraftSuggestionAppService : FieldDraftSuggestionAppService
    {
        public TestableFieldDraftSuggestionAppService(IChatClient chatClient, ILogger<FieldDraftSuggestionAppService> logger)
            : base(chatClient, logger)
        {
        }

        protected override Task CheckDraftPermissionAsync() => Task.CompletedTask;
    }

    private static FieldDraftSuggestionAppService CreateService(IChatClient chatClient)
        => new TestableFieldDraftSuggestionAppService(chatClient, NullLogger<FieldDraftSuggestionAppService>.Instance);

    private static IChatClient ChatClientReturning(string responseText)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)])));
        return fake;
    }

    private static IChatClient ChatClientThrowing(Exception ex)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(ex));
        return fake;
    }

    [Fact]
    public async Task Maps_full_draft_for_new_field()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Contract Amount\",\"name\":\"contract_amount\",\"dataType\":\"number\",\"isRequired\":true,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "Extract contract total amount", ForNewField = true });

        draft.DisplayName.ShouldBe("Contract Amount");
        draft.Name.ShouldBe("contract_amount");
        draft.DataType.ShouldBe(FieldDataType.Number);
        draft.IsRequired.ShouldBeTrue();
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Sanitizes_name_from_llm_output()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Party Name\",\"name\":\"Party Name!\",\"dataType\":\"text\",\"isRequired\":false,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "Party name", ForNewField = true });

        // Do not trust LLM output: lowercase it, collapse non-[a-z0-9] characters to underscores, merge
        // repeats, and trim leading / trailing underscores.
        draft.Name.ShouldBe("party_name");
    }

    [Fact]
    public async Task Never_suggests_name_when_editing_existing_field()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Amount\",\"name\":\"contract_amount\",\"dataType\":\"number\",\"isRequired\":false,\"allowMultiple\":false}"));

        // Guardrail 1: editing an existing field. Name is a contract-level frozen identity key and always
        // returns empty, even when the LLM suggests one.
        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "Amount", ForNewField = false });

        draft.Name.ShouldBe(string.Empty);
        draft.DisplayName.ShouldBe("Amount");
        draft.DataType.ShouldBe(FieldDataType.Number);
    }

    [Fact]
    public async Task Forces_allow_multiple_false_for_non_text()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Dates\",\"name\":\"dates\",\"dataType\":\"date\",\"isRequired\":false,\"allowMultiple\":true}"));

        // Guardrail 2: mirror FieldDefinition.ValidateMultiValue. Multi-value is valid only for Text; non-text
        // is clamped to false.
        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "All dates", ForNewField = true });

        draft.DataType.ShouldBe(FieldDataType.Date);
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Keeps_allow_multiple_true_for_text()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Tags\",\"name\":\"tags\",\"dataType\":\"text\",\"isRequired\":false,\"allowMultiple\":true}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "Tag list", ForNewField = true });

        draft.DataType.ShouldBe(FieldDataType.Text);
        draft.AllowMultiple.ShouldBeTrue();
    }

    [Fact]
    public async Task Tolerates_string_boolean_from_weak_provider()
    {
        // #264 review #4: weak structured providers may return boolean values as strings; they should not
        // silently degrade to false.
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Amount\",\"name\":\"amount\",\"dataType\":\"number\",\"isRequired\":\"true\",\"allowMultiple\":\"false\"}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "Amount", ForNewField = true });

        draft.IsRequired.ShouldBeTrue();
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Tolerates_numeric_boolean_from_weak_provider()
    {
        // #264 review #4: numeric 1/0 booleans are recognized too. allowMultiple is valid only for Text;
        // here text + 1 becomes true.
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Tags\",\"name\":\"tags\",\"dataType\":\"text\",\"isRequired\":0,\"allowMultiple\":1}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "Tags", ForNewField = true });

        draft.IsRequired.ShouldBeFalse();
        draft.AllowMultiple.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_data_type_falls_back_to_text()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"X\",\"name\":\"x\",\"dataType\":\"currency\",\"isRequired\":false,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        draft.DataType.ShouldBe(FieldDataType.Text);
    }

    [Fact]
    public async Task Strips_control_chars_from_display_name()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Line\\nBreak\\tField\",\"name\":\"x\",\"dataType\":\"text\",\"isRequired\":false,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        // Control characters collapse to a single space, matching the entity-level ValidateDisplayName rejection
        // domain and preventing drafted values from failing loudly as soon as they are saved.
        draft.DisplayName.ShouldBe("Line Break Field");
    }

    [Fact]
    public async Task Returns_empty_draft_when_output_is_not_json()
    {
        var svc = CreateService(ChatClientReturning("sorry, I cannot help with that"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        draft.DisplayName.ShouldBe(string.Empty);
        draft.Name.ShouldBe(string.Empty);
        draft.DataType.ShouldBe(FieldDataType.Text);
        draft.IsRequired.ShouldBeFalse();
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Returns_empty_draft_when_llm_throws()
    {
        var svc = CreateService(ChatClientThrowing(new InvalidOperationException("LLM down")));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        draft.DisplayName.ShouldBe(string.Empty);
        draft.Name.ShouldBe(string.Empty);
    }
}
