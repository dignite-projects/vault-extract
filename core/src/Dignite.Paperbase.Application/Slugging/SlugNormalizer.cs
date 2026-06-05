using System.Text;
using System.Text.RegularExpressions;
using Dignite.Paperbase.Documents.Fields;

namespace Dignite.Paperbase.Slugging;

/// <summary>
/// 把任意原始文本兜底归一化为 <c>[a-z0-9_]</c> 的 snake_case slug——**不信任 LLM 输出**的安全边界。
/// <para>
/// 由 <see cref="SlugSuggestionAppService"/>（DisplayName → slug，#190）与
/// <see cref="Dignite.Paperbase.Documents.Fields.FieldDraftSuggestionAppService"/>（提示词起草建议的 Name，#264）
/// 共用——Name/TypeCode 机器键的派生 sanitize 逻辑单点维护，杜绝两处漂移导致一边漏过非法字符。
/// </para>
/// </summary>
internal static class SlugNormalizer
{
    /// <summary>
    /// 小写化、非 <c>[a-z0-9]</c> 折叠成单下划线、去首尾下划线、截断到
    /// <see cref="FieldDefinitionConsts.MaxNameLength"/>（64，FieldDefinition.Name 与 DocumentType.TypeCode 单段两套白名单中较紧的上限）。
    /// 输入为空 / sanitize 后无合法字符（如未翻译的纯 CJK）→ 返回空字符串（调用方回退本地占位）。
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lowered = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
            }
            else
            {
                // 空格 / 短横线 / 标点 / CJK 等一律折叠为下划线占位，下一步再合并。
                sb.Append('_');
            }
        }

        var collapsed = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (collapsed.Length > FieldDefinitionConsts.MaxNameLength)
        {
            collapsed = collapsed[..FieldDefinitionConsts.MaxNameLength].Trim('_');
        }

        return collapsed;
    }
}
