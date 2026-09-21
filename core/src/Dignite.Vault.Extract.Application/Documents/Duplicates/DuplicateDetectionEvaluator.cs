using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Duplicates;

/// <summary>
/// The per-document answer to "does this document collide under its type's current duplicate scope?" (#651 §4).
/// <para>
/// Before #651 the decision was inline in the field-extraction write phase, its only caller. It has two write
/// callers now — that write phase (<c>FieldExtractionService</c>) and the operator's manual field correction
/// (<c>DocumentAppService.UpdateExtractedFieldsAsync</c>, §6) — plus the detail panel, which needs only the
/// scope (<see cref="ResolveScopeAsync"/>) because it runs its own query to <b>name</b> the candidates. Neither
/// write path may carry its own copy of the predicate: the pipeline and the panel drifting apart over exactly
/// this question is what #651 exists to fix.
/// </para>
/// <para>
/// <b>Scope reconciliation deliberately does NOT use this class.</b> It answers the same question for a whole
/// type, and one indexed query per document would be one round trip per row; it goes through
/// <see cref="DuplicateScopeVerdictCalculator"/> instead, which decides a whole page from a single aggregate
/// bounded by that page's fingerprints. Same verdict, different cost model — the split is by design, not
/// oversight, and the two are kept honest by the reconciliation tests asserting the same outcomes this class
/// produces per document.
/// </para>
/// <para>
/// Two short-circuits are part of the answer, not the callers' business:
/// </para>
/// <list type="bullet">
///   <item><b>A null fingerprint is never a duplicate</b> — the type declares no unique key, or this document's
///   key is partial. There is nothing to compare.</item>
///   <item><b><see cref="Document.DuplicateAllowed"/> is never re-flagged</b> — the operator reviewed this
///   document and decided it is not a duplicate (#411), and that verdict survives re-extraction. The collision
///   query is skipped entirely rather than run and discarded. Note the companion rule that keeps this honest
///   lives on the aggregate: <see cref="Document.SetFieldFingerprint"/> withdraws the override the moment the key
///   actually changes, so the short-circuit can only suppress a re-flag for the values the operator reviewed.</item>
/// </list>
/// <para>
/// The read scope is <b>always</b> <see cref="DocumentAccessScope.Unrestricted"/>: this only counts, it runs on
/// background paths with no principal, and a duplicate the uploader may not see is still a duplicate (#635). The
/// caller-facing narrowing lives in <c>DocumentAppService</c>'s panel, which is a different question.
/// </para>
/// </summary>
public class DuplicateDetectionEvaluator : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ILogger<DuplicateDetectionEvaluator> _logger;

    public DuplicateDetectionEvaluator(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        ILogger<DuplicateDetectionEvaluator> logger)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _logger = logger;
    }

    /// <summary>
    /// The whole question in one call, and the <b>only</b> member the two write paths use: reads the type's
    /// <b>current</b> <see cref="DocumentType.DuplicateScope"/> and answers the collision question with it.
    /// Resolving at call time rather than from a captured value is what makes two scope switches in quick
    /// succession converge on the later one instead of racing.
    /// <para>
    /// Callers: <c>FieldExtractionService</c>'s write phase, after it recomputes the fingerprint, and
    /// <c>DocumentAppService.UpdateExtractedFieldsAsync</c>, but only when a manual correction actually changed
    /// the key.
    /// </para>
    /// <para>
    /// An untyped document, or one whose type row has gone, is not a duplicate of anything: fields hang off
    /// document types, so with no type there is no key and no candidate set. Logged at debug rather than thrown —
    /// the callers are write paths that must still complete.
    /// </para>
    /// </summary>
    public virtual async Task<bool> EvaluateAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentTypeId is not { } documentTypeId)
        {
            return false;
        }

        var detectionScope = await ResolveScopeAsync(documentTypeId, cancellationToken);
        return await CollidesAsync(document, detectionScope, cancellationToken);
    }

    /// <summary>
    /// The type's current detection scope, defaulting to <see cref="DuplicateDetectionScope.Layer"/> when the type
    /// cannot be resolved. <c>Layer</c> is the fail-safe direction here: it is the pre-#651 behaviour and the
    /// wider of the two, so an unresolvable type can only over-report a collision into the review queue, never
    /// silently release a real duplicate downstream.
    /// <para>
    /// Public because one caller needs the scope without the verdict: <c>DocumentAppService</c>'s duplicate
    /// panel, which applies the type's detection scope to its own candidate query before narrowing that by the
    /// caller's read scope (§3). It reads the setting live, so a scope switch shows in the panel immediately
    /// rather than waiting for reconciliation to catch the persisted bit up.
    /// </para>
    /// </summary>
    public virtual async Task<DuplicateDetectionScope> ResolveScopeAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken = default)
    {
        var documentType = await _documentTypeRepository.FindAsync(documentTypeId, includeDetails: false, cancellationToken);
        if (documentType != null)
        {
            return documentType.DuplicateScope;
        }

        _logger.LogDebug(
            "Duplicate detection could not resolve DocumentType {DocumentTypeId}; falling back to the layer-wide scope.",
            documentTypeId);
        return DuplicateDetectionScope.Layer;
    }

    /// <summary>
    /// The collision question against an already-resolved <paramref name="detectionScope"/> — the second half of
    /// <see cref="EvaluateAsync"/>, split out so the two short-circuits and the query sit apart from the scope
    /// lookup.
    /// <para>
    /// <c>protected</c>, not public: nothing outside this class should be able to supply its own scope and get a
    /// verdict under it, because the whole point of #651's "read the setting at execution time" rule is that the
    /// scope is not the caller's to choose. A subclass overriding the query keeps its seam.
    /// </para>
    /// </summary>
    protected virtual async Task<bool> CollidesAsync(
        Document document,
        DuplicateDetectionScope detectionScope,
        CancellationToken cancellationToken = default)
    {
        if (document.DocumentTypeId is not { } documentTypeId
            || document.FieldFingerprint is not { } fingerprint
            || document.DuplicateAllowed)
        {
            return false;
        }

        var candidates = await _documentRepository.FindDuplicateCandidatesAsync(
            document.Id,
            documentTypeId,
            fingerprint,
            DocumentConsts.MaxDuplicateCandidates,
            detectionScope,
            document.CreatorId,
            // #635: unrestricted, explicitly. This runs with no principal and only counts — narrowing here would
            // make a blocking review reason depend on who happened to upload the other copy.
            DocumentAccessScope.Unrestricted,
            cancellationToken);

        return candidates.Count > 0;
    }
}
