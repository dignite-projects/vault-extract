using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Vault.Extract.Documents;
using Riok.Mapperly.Abstractions;
using Volo.Abp.Data;
using Volo.Abp.Mapperly;

namespace Dignite.Vault.Extract;

/// <summary>
/// Document -> DocumentDto。
/// <para>
/// <c>DocumentTypeCode</c> (external wire-format) and <c>ExtractedFields</c> dictionary keys (field names) are Id -> code/name lookup projections.
/// Internally, #207 stores <see cref="Document.DocumentTypeId"/> and <see cref="DocumentExtractedField.FieldDefinitionId"/>.
/// These require batch joins that traverse soft-delete, which the mapper cannot complete independently. Therefore
/// <see cref="MapperIgnoreTargetAttribute"/> ignores them and <c>DocumentAppService</c> batch-fills them with no N+1.
/// <c>ExtractionMetadata</c> on Document is a typed JSON value object and is <b>not exported as a whole</b> because it is internal provenance.
/// Its integrity quality signals are exported through <c>ExtractionIsComplete</c> / <c>ExtractionIncompleteReason</c> (#268),
/// also ignored here and filled by <c>DocumentAppService</c>. Null metadata (historical / digital-native) is treated as complete (<c>?? true</c>).
/// </para>
/// <para>
/// #216: PipelineRuns was removed from the DocumentDto export contract. <see cref="DocumentPipelineRunToDocumentPipelineRunDtoMapper"/>
/// is no longer nested through <c>[UseMapper]</c>; the independent <c>IDocumentPipelineRunAppService</c> now calls its <c>Map</c> directly.
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentDtoMapper : MapperBase<Document, DocumentDto>
{
    [MapperIgnoreTarget(nameof(DocumentDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractedFields))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIsComplete))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIncompleteReason))]
    [MapperIgnoreTarget(nameof(DocumentDto.RequiresReview))]
    [MapperIgnoreTarget(nameof(DocumentDto.ReviewReasonDetails))]
    public override partial DocumentDto Map(Document source);

    [MapperIgnoreTarget(nameof(DocumentDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractedFields))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIsComplete))]
    [MapperIgnoreTarget(nameof(DocumentDto.ExtractionIncompleteReason))]
    [MapperIgnoreTarget(nameof(DocumentDto.RequiresReview))]
    [MapperIgnoreTarget(nameof(DocumentDto.ReviewReasonDetails))]
    public override partial void Map(Document source, DocumentDto destination);
}

/// <summary>
/// DocumentPipelineRun -> DocumentPipelineRunDto.
/// <see cref="MapExtraPropertiesAttribute"/> passes through the generic <c>ExtraProperties</c> bag, and also deserializes
/// <c>ExtraProperties["Candidates"]</c> into strongly typed <see cref="DocumentPipelineRunDto.Candidates"/>,
/// so the frontend / downstream HttpApi.Client does not have to cast by string key.
/// <para>
/// <b>Why <c>AfterMap</c> is called manually inside <c>Map</c></b>: auto-wiring for <c>MapperBase.AfterMap</c>
/// is done by ABP <c>MapperlyAutoObjectMappingProvider</c> at the IObjectMapper layer, <b>not</b> by the Mapperly source generator.
/// This mapper is a child mapper nested through <c>[UseMapper]</c> by <see cref="DocumentToDocumentDtoMapper"/>.
/// Mapperly calls <c>Map(source)</c> directly and bypasses the IObjectMapper layer, so <c>AfterMap</c> is not triggered automatically.
/// Calling it manually inside the <c>Map</c> wrapper is currently the only reliable option.
/// Upstream position: the Riok.Mapperly team does not plan attribute-based auto-wire because their philosophy is explicit over implicit
/// (see Mapperly Discussion #1421); the ABP team considers the current behavior by design (see abpframework/abp#24592).
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

        // Within the same UoW before a persistence round-trip, this is the original written type.
        if (raw is IReadOnlyList<PipelineRunCandidate> alreadyTyped)
        {
            return alreadyTyped;
        }

        // When read back from EF Core / ABP persistence, ExtraProperties values are JsonElement.
        // See docs/en/pipeline/pipeline-runs.md "Server-Side Notes".
        if (raw is JsonElement json && json.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<PipelineRunCandidate>>(json.GetRawText());
        }

        return null;
    }
}

/// <summary>
/// Document -> DocumentListItemDto. <c>DocumentTypeCode</c> and <c>ExtractedFields</c> are batch-filled by
/// <c>DocumentAppService</c>, same as <see cref="DocumentToDocumentDtoMapper"/>: Id -> code/name joins that traverse soft-delete,
/// resolved once after pagination with no N+1.
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentListItemDtoMapper : MapperBase<Document, DocumentListItemDto>
{
    [MapperIgnoreTarget(nameof(DocumentListItemDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentListItemDto.ExtractedFields))]
    [MapperIgnoreTarget(nameof(DocumentListItemDto.RequiresReview))]
    public override partial DocumentListItemDto Map(Document source);

    [MapperIgnoreTarget(nameof(DocumentListItemDto.DocumentTypeCode))]
    [MapperIgnoreTarget(nameof(DocumentListItemDto.ExtractedFields))]
    [MapperIgnoreTarget(nameof(DocumentListItemDto.RequiresReview))]
    public override partial void Map(Document source, DocumentListItemDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentTypeToDtoMapper : MapperBase<DocumentType, DocumentTypeDto>
{
    public override partial DocumentTypeDto Map(DocumentType source);
    public override partial void Map(DocumentType source, DocumentTypeDto destination);
}

/// <summary>
/// FieldDefinition -> FieldDefinitionDto. All scalar values, including immutable <see cref="FieldDefinition.DocumentTypeId"/> (#207),
/// are mapped directly by Mapperly with no lookup projection.
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class FieldDefinitionToDtoMapper : MapperBase<FieldDefinition, FieldDefinitionDto>
{
    public override partial FieldDefinitionDto Map(FieldDefinition source);
    public override partial void Map(FieldDefinition source, FieldDefinitionDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class CabinetToDtoMapper : MapperBase<Cabinet, CabinetDto>
{
    public override partial CabinetDto Map(Cabinet source);
    public override partial void Map(Cabinet source, CabinetDto destination);
}

/// <summary>
/// DocumentStatisticsModel -> DocumentStatisticsDto (#333). A flat scalar projection (overview counts +
/// total storage bytes); all property names match 1:1 so Mapperly maps them directly with no lookup.
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentStatisticsToDtoMapper : MapperBase<DocumentStatisticsModel, DocumentStatisticsDto>
{
    public override partial DocumentStatisticsDto Map(DocumentStatisticsModel source);
    public override partial void Map(DocumentStatisticsModel source, DocumentStatisticsDto destination);
}

/// <summary>
/// DuplicateCandidateModel -> DuplicateCandidateDto (#411). A flat scalar projection (Id / Title / FileName /
/// CreationTime); all property names match 1:1 so Mapperly maps them directly with no lookup.
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DuplicateCandidateToDtoMapper : MapperBase<DuplicateCandidateModel, DuplicateCandidateDto>
{
    public override partial DuplicateCandidateDto Map(DuplicateCandidateModel source);
    public override partial void Map(DuplicateCandidateModel source, DuplicateCandidateDto destination);
}
