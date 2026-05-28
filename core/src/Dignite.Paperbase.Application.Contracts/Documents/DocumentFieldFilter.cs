using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Dignite.Paperbase.Documents.Fields;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 单个 ExtractedFields 字段过滤条件（<see cref="GetDocumentListInput.FieldFilters"/> 列表元素，
/// 同时是 MCP 检索 tool 的 LLM-facing 入参元素）。一次检索可传多个，它们之间取 <c>AND</c>（全部满足），
/// 且都锚定在同一个 <c>documentTypeCode</c> 内。字段声明类型由服务端从 <c>(documentTypeCode, Name)</c> 的
/// <c>FieldDefinition</c> 解析——调用方不传类型。
/// <see cref="Value"/>（等值）与 <see cref="Min"/> / <see cref="Max"/>（区间）至少给其一；
/// 区间只对 Number / Date / DateTime 字段有意义，String / Boolean 只认等值。
/// </summary>
public class DocumentFieldFilter : IValidatableObject
{
    [Required]
    [RegularExpression(FieldDefinitionConsts.NamePattern)]
    [Description("Name of an extracted field to filter by. Must be a field defined on the document type.")]
    public string? Name { get; set; }

    [StringLength(DocumentConsts.MaxSearchFieldValueLength)]
    [Description("Exact value the field must equal. Use this for String and Boolean fields "
        + "(which support equality only). Optional.")]
    public string? Value { get; set; }

    [StringLength(DocumentConsts.MaxSearchFieldValueLength)]
    [Description("Inclusive lower bound for a field-value range. Only Number, Date and DateTime "
        + "fields support ranges (passing a range on a String/Boolean field is rejected). Optional.")]
    public string? Min { get; set; }

    [StringLength(DocumentConsts.MaxSearchFieldValueLength)]
    [Description("Inclusive upper bound for a field-value range. Pairs with Min. Optional.")]
    public string? Max { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // 至少给一个值（等值或区间下/上界），否则是残缺过滤器——loud fail（AbpValidationException），
        // 不静默退化成"该类型全捞"。
        if (Value == null && Min == null && Max == null)
        {
            yield return new ValidationResult(
                "A field filter must specify a Value, or a Min/Max range.",
                new[] { nameof(Value), nameof(Min), nameof(Max) });
        }
    }
}
