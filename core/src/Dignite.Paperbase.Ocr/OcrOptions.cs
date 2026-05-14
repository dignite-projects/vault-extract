using System.Collections.Generic;

namespace Dignite.Paperbase.Ocr;

public class OcrOptions
{
    /// <summary>语言提示列表（BCP 47 格式）。空列表表示自动检测。</summary>
    public IList<string> LanguageHints { get; set; } = new List<string>();

    /// <summary>文件 MIME 类型，帮助部分 Provider 优化识别策略。</summary>
    public string ContentType { get; set; } = string.Empty;
}
