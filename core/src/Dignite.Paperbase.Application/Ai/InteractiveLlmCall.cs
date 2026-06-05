using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dignite.Paperbase.Ai;

/// <summary>
/// 交互式 request/response LLM 助手共用的「超时 + fail-open」信封（#264 review #10）。
/// <para>
/// Paperbase 现有两个同步 LLM 起草助手——<c>SlugSuggestionAppService</c>（DisplayName → slug）与
/// <c>FieldDraftSuggestionAppService</c>（提示词 → 字段元数据草稿），二者的安全控制流完全一致：
/// 把调用方取消令牌（ABP 从 <c>HttpContext.RequestAborted</c> 注入）与一个服务端 deadline 链接、
/// 区分「客户端取消」（原样上抛）与「服务端超时 / provider 故障」（记 warning + 回退），
/// 不让 LLM 不可用拖死 admin 交互。把这段安全关键的外壳收敛到单点，避免两处复制后静默漂移
/// （改了一处 deadline / 取消分流语义，另一处忘改 → 削弱已声明的 fail-open 保证）。
/// </para>
/// <para>
/// 仅封装外壳——每个调用点仍各自显式持有 prompt、ResponseFormat 与解析逻辑
/// （.claude/rules/llm-call-anti-patterns.md 要求每个 LLM 入口的指令 / 解析可审计、不抽象掉）。
/// </para>
/// </summary>
internal static class InteractiveLlmCall
{
    /// <summary>
    /// 调用 <paramref name="chatClient"/> 并返回原始响应文本；客户端取消原样上抛，服务端超时 / 其他异常记 warning 后返回 <c>null</c>
    /// （调用方据 null 回退到各自的保守默认）。
    /// </summary>
    public static async Task<string?> TryGetResponseTextAsync(
        IChatClient chatClient,
        IReadOnlyList<ChatMessage> messages,
        ChatResponseFormat responseFormat,
        TimeSpan timeout,
        ILogger logger,
        string callName,
        CancellationToken cancellationToken)
    {
        var options = new ChatOptions { ResponseFormat = responseFormat };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
            return response.Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 客户端主动断开 / 取消 —— 正常情况，原样向上抛（按取消语义结束请求），不记为 LLM 失败、不产生日志噪音。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 服务端 deadline 触发（LLM 太慢 / provider 不响应取消）—— 返回 null，调用方回退保守默认。
            logger.LogWarning(
                "{CallName} timed out after {TimeoutSeconds}s; returning null for fallback.",
                callName, (int)timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // LLM 不可用不应让 admin 卡死 —— 返回 null，调用方回退保守默认。
            logger.LogWarning(ex, "{CallName} LLM call failed; returning null for fallback.", callName);
            return null;
        }
    }
}
