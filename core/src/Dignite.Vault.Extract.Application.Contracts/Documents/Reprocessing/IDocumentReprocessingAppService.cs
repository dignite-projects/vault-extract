using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Reprocessing;

/// <summary>
/// Bulk reprocessing for existing documents (#289): rerun existing documents after configuration
/// changes such as classification prompts / field definitions. Both kinds share the same execution
/// base: manual trigger + preview + chained dispatch + per-document idempotence. They differ by which
/// pipeline runs, scope, cascade depth, and warning severity:
/// <list type="bullet">
///   <item><b>Field re-extraction</b> (leaf, safe, light warning): fixed document-type scope; only
///   runs <c>field-extraction</c> and does not reclassify.</item>
///   <item><b>Reclassification</b> (cascading, destructive, heavy warning): human-selected scope; runs
///   <c>classification</c>, whose success necessarily cascades field re-extraction, and protects
///   manual confirmations by default.</item>
/// </list>
/// <para>Human owns the judgment (#289): the system does not guess whether reprocessing is needed or how broad the scope should be. Configuration changes have zero cascade; humans can trigger reprocessing at any time.</para>
/// </summary>
public interface IDocumentReprocessingAppService : IApplicationService
{
    /// <summary>Field re-extraction preview: affected document count plus the current field list for that type.</summary>
    Task<FieldReextractionPreviewDto> PreviewFieldExtractionAsync(Guid documentTypeId);

    /// <summary>Triggers field re-extraction by enqueueing a dispatcher for the type scope and immediately returning the estimated document count.</summary>
    Task<ReprocessingStartResultDto> StartFieldExtractionAsync(StartFieldReextractionInput input);

    /// <summary>Reclassification preview: counts affected documents by scope plus the protect-manual-confirmation toggle.</summary>
    Task<ReclassificationPreviewDto> PreviewReclassificationAsync(ReclassificationScopeInput input);

    /// <summary>Triggers reclassification by enqueueing a dispatcher for the scope and immediately returning the estimated document count.</summary>
    Task<ReprocessingStartResultDto> StartReclassificationAsync(ReclassificationScopeInput input);
}
