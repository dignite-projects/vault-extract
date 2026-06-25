using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Verifies that workflows fetch prompts from IPromptProvider on every RunAsync call instead of freezing
/// them in the constructor. This ensures dynamic IPromptProvider changes, such as language switches or
/// tenant customization, take effect on the next call without restarting the host.
/// </summary>
public class DocumentWorkflowPromptLifetime_Tests
{
    [Fact]
    public async Task ClassificationWorkflow_GetClassificationPrompt_CalledOnEachRunAsync()
    {
        var inner = BuildChatClientReturning(
            """{"typeCode":null,"confidence":0,"reason":"none","candidates":[]}""");

        var promptProvider = Substitute.For<IPromptProvider>();
        promptProvider.GetClassificationPrompt(Arg.Any<string>())
            .Returns(new PromptTemplate("Prompt-A"), new PromptTemplate("Prompt-B"));

        var workflow = new DocumentClassificationWorkflow(
            inner,
            Options.Create(new ExtractBehaviorOptions()),
            promptProvider);

        var types = new List<DocumentType>
            { new DocumentType(Guid.NewGuid(), null, "contract.general", "合同") };

        await workflow.RunAsync(types, "text one");
        await workflow.RunAsync(types, "text two");

        // Prompt is fetched from provider on every call, not once at construction.
        promptProvider.Received(2).GetClassificationPrompt(Arg.Any<string>());
    }

    [Fact]
    public async Task ClassificationWorkflow_SystemInstructions_ReflectFreshPromptOnEachCall()
    {
        var capturedSystemMessages = new List<string>();
        var inner = BuildChatClientCapturing(
            capturedSystemMessages,
            """{"typeCode":null,"confidence":0,"reason":"none","candidates":[]}""");

        var promptProvider = Substitute.For<IPromptProvider>();
        promptProvider.GetClassificationPrompt(Arg.Any<string>())
            .Returns(new PromptTemplate("Prompt-A"), new PromptTemplate("Prompt-B"));

        var workflow = new DocumentClassificationWorkflow(
            inner,
            Options.Create(new ExtractBehaviorOptions()),
            promptProvider);

        var types = new List<DocumentType>
            { new DocumentType(Guid.NewGuid(), null, "contract.general", "合同") };

        await workflow.RunAsync(types, "text");
        await workflow.RunAsync(types, "text");

        capturedSystemMessages.Count.ShouldBe(2);
        capturedSystemMessages[0].ShouldContain("Prompt-A");
        capturedSystemMessages[1].ShouldContain("Prompt-B");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IChatClient BuildChatClientReturning(string responseText)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)])));
        return inner;
    }

    private static IChatClient BuildChatClientCapturing(
        List<string> capturedSystemMessages,
        string responseText)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var msgs = call.Arg<IEnumerable<ChatMessage>>().ToList();
                var opts = call.Arg<ChatOptions?>();

                // ChatClientAgent passes system instructions via ChatOptions.Instructions
                // (Microsoft.Extensions.AI ≥ 10.5) rather than as a ChatRole.System message.
                // Collect both sources so the assertion stays correct across versions.
                var allText = string.Join(" ",
                    msgs.Select(m => m.Text ?? "").Append(opts?.Instructions ?? ""));

                capturedSystemMessages.Add(allText);
                return Task.FromResult(
                    new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
            });
        return inner;
    }
}
