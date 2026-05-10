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
    AIFunction Create(
        DocumentChatToolContext ctx,
        Delegate method,
        string name,
        string description);

    /// <summary>
    /// Issue #116: opt-in overload that lets the caller supply a sanitized,
    /// user-facing description of an in-flight call (e.g. <c>"正在按文档类型 'X' 向量检索…"</c>).
    /// Surfaces in the streaming <c>ToolCallStarted</c> event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default implementation forwards to the four-arg
    /// <see cref="Create(DocumentChatToolContext, Delegate, string, string)"/>
    /// and discards the describer — preserving binary compatibility with external
    /// implementers compiled against the pre-Issue-#116 interface (Issue #130
    /// post-mortem: PR #129 originally mutated the existing signature, which
    /// breaks pre-built modules loaded against an upgraded core package).
    /// First-party <c>DocumentChatToolFactory</c> overrides this overload to
    /// actually route the describer into the audit wrapper.
    /// </para>
    /// <para>
    /// SECURITY: <paramref name="progressDescriber"/> output reaches end users
    /// via SSE BEFORE the tool body runs. It MUST NOT echo raw model arguments
    /// — see <c>.claude/rules/doc-chat-anti-patterns.md</c> reverse example C #4
    /// and the Issue #130 post-mortem.
    /// </para>
    /// </remarks>
    AIFunction Create(
        DocumentChatToolContext ctx,
        Delegate method,
        string name,
        string description,
        Func<IReadOnlyDictionary<string, object?>, string?>? progressDescriber)
        => Create(ctx, method, name, description);
}
