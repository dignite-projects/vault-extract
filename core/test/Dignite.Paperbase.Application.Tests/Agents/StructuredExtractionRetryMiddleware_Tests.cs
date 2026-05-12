using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Agents;

/// <summary>
/// Unit tests for <see cref="StructuredExtractionRetryMiddleware.WithValidationRetry{T}"/>.
/// Uses a substituted <see cref="IChatClient"/> wrapped in a real
/// <see cref="ChatClientAgent"/>, so the middleware exercises the actual MAF
/// agent pipeline rather than a hand-rolled stub.
/// </summary>
public class StructuredExtractionRetryMiddleware_Tests
{
    public sealed class TestResult
    {
        public int Value { get; set; }
    }

    private sealed class NonNegativeValueValidator : IExtractionValidator<TestResult>
    {
        public int InvocationCount { get; private set; }

        public ExtractionValidationResult Validate(TestResult result)
        {
            InvocationCount++;
            return result.Value < 0
                ? ExtractionValidationResult.Failed($"Value must be non-negative; got {result.Value}")
                : ExtractionValidationResult.Ok();
        }
    }

    /// <summary>
    /// Builds an <see cref="IChatClient"/> substitute that returns each text entry in order:
    /// 1st call → texts[0], 2nd → texts[1], etc. Captures every messages list it received
    /// into <paramref name="capturedRequests"/> so assertions can verify feedback injection.
    /// </summary>
    private static IChatClient BuildSequencedChatClient(
        List<IReadOnlyList<ChatMessage>> capturedRequests,
        params string[] texts)
    {
        var client = Substitute.For<IChatClient>();
        var index = 0;
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var msgs = call.Arg<IEnumerable<ChatMessage>>().ToList();
                capturedRequests.Add(msgs);
                var text = texts[System.Math.Min(index, texts.Length - 1)];
                index++;
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
            });
        return client;
    }

    private static AIAgent BuildWrappedAgent(IChatClient chatClient, IExtractionValidator<TestResult> validator, int maxRetries = 1)
    {
        var inner = new ChatClientAgent(chatClient);
        return inner.WithValidationRetry(validator, NullLogger.Instance, maxRetries);
    }

    [Fact]
    public async Task Happy_Path_Calls_Inner_Once_And_Skips_Retry()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var client = BuildSequencedChatClient(requests, "{\"value\": 5}");
        var validator = new NonNegativeValueValidator();
        var agent = BuildWrappedAgent(client, validator);

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "extract")]);

        requests.Count.ShouldBe(1);
        validator.InvocationCount.ShouldBe(1);
        response.Messages.Last().Text.ShouldContain("\"value\": 5");
    }

    [Fact]
    public async Task Invalid_Result_Triggers_One_Retry_And_Recovers()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var client = BuildSequencedChatClient(
            requests,
            "{\"value\": -7}",   // 1st call: invalid
            "{\"value\": 42}");  // 2nd call after feedback: valid
        var validator = new NonNegativeValueValidator();
        var agent = BuildWrappedAgent(client, validator);

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "extract")]);

        requests.Count.ShouldBe(2);
        validator.InvocationCount.ShouldBe(2);
        response.Messages.Last().Text.ShouldContain("\"value\": 42");
    }

    [Fact]
    public async Task Retry_Request_Includes_Validator_Error_Feedback()
    {
        // The retry path must surface the validator's error message verbatim to the LLM
        // so the model can self-correct. This is the contract the validator's Errors
        // documentation depends on.
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var client = BuildSequencedChatClient(
            requests,
            "{\"value\": -7}",
            "{\"value\": 0}");
        var agent = BuildWrappedAgent(client, new NonNegativeValueValidator());

        await agent.RunAsync([new ChatMessage(ChatRole.User, "extract")]);

        requests.Count.ShouldBe(2);
        // The second request must include a user message carrying the validator's error.
        var secondRequest = requests[1];
        secondRequest.Any(m =>
            m.Role == ChatRole.User &&
            (m.Text ?? string.Empty).Contains("Value must be non-negative; got -7")).ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_Json_Triggers_Retry()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var client = BuildSequencedChatClient(
            requests,
            "not json at all",
            "{\"value\": 1}");
        var validator = new NonNegativeValueValidator();
        var agent = BuildWrappedAgent(client, validator);

        await agent.RunAsync([new ChatMessage(ChatRole.User, "extract")]);

        requests.Count.ShouldBe(2);
        // Validator only ever sees parsed objects, so first call never reaches it.
        validator.InvocationCount.ShouldBe(1);
        // The retry message should signal that the previous response wasn't valid JSON.
        var secondRequest = requests[1];
        secondRequest.Any(m =>
            m.Role == ChatRole.User &&
            (m.Text ?? string.Empty).Contains("not valid JSON")).ShouldBeTrue();
    }

    [Fact]
    public async Task Retries_Exhausted_Returns_Last_Response_Unchanged()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var client = BuildSequencedChatClient(
            requests,
            "{\"value\": -1}",
            "{\"value\": -2}");
        var validator = new NonNegativeValueValidator();
        var agent = BuildWrappedAgent(client, validator, maxRetries: 1);

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "extract")]);

        // Initial + 1 retry = 2 total invocations.
        requests.Count.ShouldBe(2);
        validator.InvocationCount.ShouldBe(2);
        // The middleware does NOT throw on retry exhaustion — it returns the final response
        // unchanged so the caller's RunAsync<T> still deserializes and downstream business
        // logic (ReviewStatus.Pending routing) handles the low-confidence result.
        response.Messages.Last().Text.ShouldContain("\"value\": -2");
    }

    [Fact]
    public async Task MaxRetries_Zero_Means_Single_Attempt_No_Retries()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var client = BuildSequencedChatClient(
            requests,
            "{\"value\": -1}");
        var agent = BuildWrappedAgent(client, new NonNegativeValueValidator(), maxRetries: 0);

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "extract")]);

        requests.Count.ShouldBe(1);
        response.Messages.Last().Text.ShouldContain("\"value\": -1");
    }
}
