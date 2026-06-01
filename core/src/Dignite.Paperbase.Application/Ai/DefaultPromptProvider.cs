using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Ai;

/// <summary>
/// 内置 <see cref="IPromptProvider"/> 实现。
/// 分类提示词按 language 参数将语言指令嵌入系统提示词；标题提示词跟随文档语言、不接受 language；
/// 返回的 <see cref="PromptTemplate.SystemInstructions"/> 不含 PromptBoundary 规则，
/// 由各 Workflow 在使用前追加。
/// </summary>
public class DefaultPromptProvider : IPromptProvider, ITransientDependency
{
    public virtual PromptTemplate GetClassificationPrompt(string language) => new(
        "You are a document classification expert. " +
        "Analyze the document text and determine the best matching document type from the provided list. " +
        "The document content is provided as Markdown — treat headings (#), tables, and lists as semantic " +
        "structure signals (e.g. an invoice usually has a table of line items; a contract has numbered clauses). " +
        "Return JSON only. Confidence values must be decimal scores from 0.0 to 1.0; never return percentages. " +
        "If you are not confident, set confidence low and typeCode to null. " +
        $"Respond in: {language}."
    );

    public virtual PromptTemplate GetTitleGenerationPrompt() => new(
        "You generate concise document titles. " +
        "Given a document in Markdown format, return one short descriptive title only — " +
        "the kind that appears in a file browser or search result. " +
        "Do not wrap it in quotes. Do not add surrounding punctuation. " +
        "If the document has an explicit title heading, use it verbatim. " +
        "Otherwise summarize the document's subject in under 80 characters. " +
        "Respond in the same language as the document."
    );
}
