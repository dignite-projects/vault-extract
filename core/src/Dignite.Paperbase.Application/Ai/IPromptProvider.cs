namespace Dignite.Paperbase.Ai;

/// <summary>
/// 为各 MAF Workflow 提供系统提示词。
/// 实现侧可按语言、租户或业务场景返回不同模板；
/// 测试侧注入替代实现以隔离 LLM 调用。
/// </summary>
public interface IPromptProvider
{
    PromptTemplate GetClassificationPrompt(string language);

    /// <summary>
    /// 标题生成提示词。<b>不</b>接受 language 参数——标题策略是「跟随文档语言」
    /// （prompt 内置 "Respond in the same language as the document."），
    /// 不受 <c>PaperbaseAIBehaviorOptions.DefaultLanguage</c> 影响。
    /// </summary>
    PromptTemplate GetTitleGenerationPrompt();
}
