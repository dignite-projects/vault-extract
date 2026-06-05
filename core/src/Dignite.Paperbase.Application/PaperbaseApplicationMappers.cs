using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Riok.Mapperly.Abstractions;
using Volo.Abp.Data;
using Volo.Abp.Mapperly;

namespace Dignite.Paperbase;

/// <summary>
/// Document -> DocumentDto。
/// <para>
/// <c>DocumentTypeCode</c>（外部 wire-format）与 <c>ExtractedFields</c> 字典 key（字段名）是 Id → code/name 的查找投影
/// （#207：内部存 <see cref="Document.DocumentTypeId"/> 与 <see cref="DocumentExtractedField.FieldDefinitionId"/>），
/// 需穿透 soft-delete 的批量 join，mapper 无法独立完成——故 <see cref="MapperIgnoreTargetAttribute"/> 忽略后由
/// <c>DocumentAppService</c> 批量填充（无 N+1）。
/// <c>ExtractionMetadata</c> 在 Document 上是 typed JSON 值对象，整体<b>不出口</b>（内部 provenance）；但其中的
/// 完整性质量信号经 <c>ExtractionIsComplete</c> / <c>ExtractionIncompleteReason</c> 出口（#268），同样 ignore 后由
/// <c>DocumentAppService</c> 填充——null metadata（历史 / 数字版）按完整处理（<c>?? true</c>）。
/// </para>
/// <para>
/// #216：PipelineRuns 已从 DocumentDto 出口契约移除，<see cref="DocumentPipelineRunToDocumentPipelineRunDtoMapper"/>
/// 不再以 <c>[UseMapper]</c> 嵌套调用，改由独立 <c>IDocumentPipelineRunAppService</c> 直接调用其 <c>Map</c>。
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentDtoMapper : MapperBase<Document, DocumentDto>
{
    [MapperIgnoreTarget(nameof(DocumentDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractedFields))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIsComplete))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIncompleteReason))]
    public override partial DocumentDto Map(Document source);

    [MapperIgnoreTarget(nameof(DocumentDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractedFields))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIsComplete))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIncompleteReason))]
    public override partial void Map(Document source, DocumentDto destination);
}

/// <summary>
/// DocumentPipelineRun -> DocumentPipelineRunDto.
/// <see cref="MapExtraPropertiesAttribute"/> 透传通用 <c>ExtraProperties</c> bag；同时把
/// <c>ExtraProperties["Candidates"]</c> 反序列化为强类型 <see cref="DocumentPipelineRunDto.Candidates"/>，
/// 让前端 / 下游 HttpApi.Client 不必按字符串 key cast。
/// <para>
/// <b>为什么手动在 <c>Map</c> 内调 <c>AfterMap</c></b>：<c>MapperBase.AfterMap</c> 的 auto-wire
/// 由 ABP <c>MapperlyAutoObjectMappingProvider</c>（IObjectMapper 层）做，<b>不是</b>
/// Mapperly source generator 做的。但本 mapper 是被 <see cref="DocumentToDocumentDtoMapper"/>
/// 通过 <c>[UseMapper]</c> 嵌套调用的子 mapper —— Mapperly 直接调 <c>Map(source)</c>，
/// 绕过 IObjectMapper layer，<c>AfterMap</c> 不会自动触发。手动在 <c>Map</c> wrapper 内
/// 调用是当前唯一可靠的方案。
/// 上游计划：Riok.Mapperly 团队不打算引入 attribute-based auto-wire（哲学：显式优于隐式，
/// 参 Mapperly Discussion #1421）；ABP 团队认为当前行为 by design（参 abpframework/abp#24592）。
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
[MapExtraProperties]
public partial class DocumentPipelineRunToDocumentPipelineRunDtoMapper : MapperBase<DocumentPipelineRun, DocumentPipelineRunDto>
{
    public override DocumentPipelineRunDto Map(DocumentPipelineRun source)
    {
        var destination = MapCore(source);
        AfterMap(source, destination);
        return destination;
    }

    public override void Map(DocumentPipelineRun source, DocumentPipelineRunDto destination)
    {
        MapCore(source, destination);
        AfterMap(source, destination);
    }

    public override void AfterMap(DocumentPipelineRun source, DocumentPipelineRunDto destination)
    {
        destination.Candidates = ExtractCandidates(source.ExtraProperties);
    }

    [MapperIgnoreTarget(nameof(DocumentPipelineRunDto.Candidates))]
    private partial DocumentPipelineRunDto MapCore(DocumentPipelineRun source);

    [MapperIgnoreTarget(nameof(DocumentPipelineRunDto.Candidates))]
    private partial void MapCore(DocumentPipelineRun source, DocumentPipelineRunDto destination);

    private static IReadOnlyList<PipelineRunCandidate>? ExtractCandidates(ExtraPropertyDictionary? extra)
    {
        if (extra is null
            || !extra.TryGetValue(PipelineRunExtraPropertyNames.ClassificationCandidates, out var raw)
            || raw is null)
        {
            return null;
        }

        // 同一 UoW 内尚未持久化往返时是写入的原始类型。
        if (raw is IReadOnlyList<PipelineRunCandidate> alreadyTyped)
        {
            return alreadyTyped;
        }

        // EF Core / ABP 持久化读回时 ExtraProperties 的 value 是 JsonElement
        // （参考 docs/pipeline-runs.md "Server-Side Notes"）。
        if (raw is JsonElement json && json.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<PipelineRunCandidate>>(json.GetRawText());
        }

        return null;
    }
}

/// <summary>
/// Document -> DocumentListItemDto。<c>DocumentTypeCode</c> 与 <c>ExtractedFields</c> 同 <see cref="DocumentToDocumentDtoMapper"/>
/// 由 <c>DocumentAppService</c> 批量填充（Id → code/name 穿透 soft-delete 的 join，分页后一次性解析，无 N+1）。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentListItemDtoMapper : MapperBase<Document, DocumentListItemDto>
{
    [MapperIgnoreTarget(nameof(DocumentListItemDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentListItemDto.ExtractedFields))]
    public override partial DocumentListItemDto Map(Document source);

    [MapperIgnoreTarget(nameof(DocumentListItemDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentListItemDto.ExtractedFields))]
    public override partial void Map(Document source, DocumentListItemDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentTypeToDtoMapper : MapperBase<DocumentType, DocumentTypeDto>
{
    public override partial DocumentTypeDto Map(DocumentType source);
    public override partial void Map(DocumentType source, DocumentTypeDto destination);
}

/// <summary>
/// FieldDefinition -> FieldDefinitionDto。所有标量（含不可变 <see cref="FieldDefinition.DocumentTypeId"/>，#207）
/// 由 Mapperly 直接映射，无需查找投影。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class FieldDefinitionToDtoMapper : MapperBase<FieldDefinition, FieldDefinitionDto>
{
    public override partial FieldDefinitionDto Map(FieldDefinition source);
    public override partial void Map(FieldDefinition source, FieldDefinitionDto destination);
}

/// <summary>
/// ExportTemplate -> ExportTemplateDto（#207）。所有标量（含不可变 <see cref="ExportTemplate.DocumentTypeId"/>）
/// 及列集合（FieldDefinitionId / ColumnName / Order）由 Mapperly 直接映射，无需查找投影。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class ExportTemplateToDtoMapper : MapperBase<ExportTemplate, ExportTemplateDto>
{
    public override partial ExportTemplateDto Map(ExportTemplate source);
    public override partial void Map(ExportTemplate source, ExportTemplateDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class CabinetToDtoMapper : MapperBase<Cabinet, CabinetDto>
{
    public override partial CabinetDto Map(Cabinet source);
    public override partial void Map(Cabinet source, CabinetDto destination);
}
