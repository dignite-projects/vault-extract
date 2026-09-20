using System;
using Dignite.Vault.Extract.Abstractions.Documents;

namespace Dignite.Vault.Extract.Documents.Pipelines.Lifecycle;

/// <summary>
/// The one place a <see cref="DocumentReadyEto"/> is shaped. Both publish sites — the lifecycle handler on a
/// transition into Ready, and <c>DocumentAppService.UpdateExtractedFieldsAsync</c> re-announcing an already-Ready
/// document (#650) — build the payload here, so a field added to the ETO cannot be added to one site and missed at
/// the other.
/// </summary>
public static class DocumentReadyEtoFactory
{
    public static DocumentReadyEto Create(Document document, string? documentTypeCode, DateTime eventTime) => new()
    {
        DocumentId = document.Id,
        TenantId = document.TenantId,
        EventTime = eventTime,
        DocumentTypeCode = documentTypeCode,
        // #306: provenance link for a Scenario B sub-document (null for normally-uploaded documents).
        OriginDocumentId = document.OriginDocumentId
    };
}
