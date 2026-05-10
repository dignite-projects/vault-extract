using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Abstractions.Chat;

/// <summary>
/// Creates document-chat AI tools with the project's shared audit, logging, and metrics behavior.
/// Business modules should use this factory instead of calling <see cref="AIFunctionFactory"/> directly.
/// </summary>
public interface IDocumentChatToolFactory
{
    /// <summary>
    /// Creates an audited document-chat tool from a .NET method delegate.
    /// </summary>
    /// <param name="ctx">Per-turn context (tenant, conversation, user, anchor document).</param>
    /// <param name="method">The .NET delegate that implements the tool body.</param>
    /// <param name="name">Stable tool name surfaced to the LLM. Must be a compile-time constant.</param>
    /// <param name="description">Tool description surfaced to the LLM. Must be a compile-time constant.</param>
    /// <param name="progressDescriber">
    /// Issue #116: optional function that returns a sanitized, user-facing description of an
    /// in-flight call (e.g. "正在按甲方 'X 公司' 查找合同"). Surfaces in the streaming
    /// <c>ToolCallStarted</c> event. Receives the raw <c>AIFunctionArguments</c>; the implementer
    /// is responsible for suppressing PII and LLM-rewritten free-form text — the returned string
    /// reaches end users, not just operators. Return <c>null</c> to fall back to a generic
    /// "正在执行 {ToolName}..." label produced by the AppService.
    /// </param>
    AIFunction Create(
        DocumentChatToolContext ctx,
        Delegate method,
        string name,
        string description,
        Func<IReadOnlyDictionary<string, object?>, string?>? progressDescriber = null);
}
