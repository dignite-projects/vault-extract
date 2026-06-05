using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 「按提示词起草字段元数据」服务（issue #264）。admin 把抽取指令作为主输入，本服务用一次 LLM 调用
/// **起草** DisplayName / DataType / IsRequired / AllowMultiple（新建字段时额外建议 Name），admin 再核对 / 修改后保存。
/// <para>
/// 与 <see cref="Dignite.Paperbase.Slugging.ISlugSuggestionAppService"/> 同属交互式 request/response 形态的 LLM 起草助手，
/// 复用同一套安全约定（CLAUDE.md "## 安全约定" / .claude/rules/llm-call-anti-patterns.md）：
/// 编译期常量 instructions、用户提示词经 <c>PromptBoundary</c> 包裹、不挂 AIContextProviders、不信任 LLM 输出（服务端 sanitize）。
/// </para>
/// </summary>
public interface IFieldDraftSuggestionAppService : IApplicationService
{
    Task<FieldDefinitionDraftDto> DraftAsync(DraftFieldDefinitionInput input, CancellationToken cancellationToken = default);
}
