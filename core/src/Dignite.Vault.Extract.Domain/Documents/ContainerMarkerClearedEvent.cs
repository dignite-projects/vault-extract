using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Local domain event raised on a true→false transition of <see cref="Document.IsContainer"/> (#349):
/// the document was previously a <b>container</b> and has now been reclassified to a concrete type
/// (operator <c>ConfirmClassification</c> reclassify, or a high-confidence automatic
/// <c>ApplyAutomaticClassificationResult</c> re-recognition). Published through
/// <c>AddLocalEvent</c> inside those methods in the same transaction as the marker clear.
/// <para>
/// A container that was already segmented spawned derived sub-documents
/// (<see cref="Document.OriginDocumentId"/> == this container's id) and recorded
/// <c>DocumentSegment</c> work-queue rows. Once the container stops being a container, those
/// sub-documents no longer have a parent that delegates to them — leaving them live would
/// double-count downstream with no retraction signal (#349). The in-process handler
/// (<c>ContainerMarkerClearedEventHandler</c>) reacts within the same transaction: it soft-deletes
/// the spawned sub-documents (emitting <c>DocumentDeletedEto</c> per sub-document so downstream
/// archives their derived data) and removes the container's <c>DocumentSegment</c> rows.
/// </para>
/// <para>
/// Valid consumption is limited to this in-process retraction hook inside the Extract channel
/// layer. Business side effects belong to downstream consumers, which subscribe to the outbound
/// <c>DocumentDeletedEto</c> in their own process instead of attaching to this local event.
/// </para>
/// </summary>
public class ContainerMarkerClearedEvent
{
    /// <summary>The document whose container marker was just cleared (the former container).</summary>
    public Guid DocumentId { get; }

    public ContainerMarkerClearedEvent(Guid documentId)
    {
        DocumentId = documentId;
    }
}
