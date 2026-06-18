using System;

namespace Dignite.DocumentAI.Documents;

/// <summary>
/// Local domain event raised on a false→true transition of <see cref="Document.IsContainer"/> <b>when the
/// document previously had a concrete type</b> (#355): a document that was already classified to a real type
/// (so downstream may have built a business record from its <c>DocumentClassifiedEto</c> / <c>DocumentReadyEto</c>)
/// is now re-recognized as a <b>container</b>, which clears that type. Published through <c>AddLocalEvent</c>
/// inside <see cref="Document.MarkAsContainer"/> in the same transaction as the marker set.
/// <para>
/// This is the mirror image of <see cref="ContainerMarkerClearedEvent"/> (#349, the container→type direction).
/// Without a signal, downstream would keep the now-invalid typed record <b>and</b> additionally consume the
/// sub-documents the container goes on to spawn — a double-count. The in-process handler
/// (<c>ContainerMarkerSetEventHandler</c>) reacts within the same transaction by publishing the outbound
/// <c>DocumentReclassifiedToContainerEto</c>, telling downstream to retract the record derived from the former type.
/// </para>
/// <para>
/// Gated on a real type→container transition: a fresh upload first detected as a container had no prior type and
/// no downstream record, so it raises nothing. Valid consumption is limited to this in-process publish hook inside
/// the DocumentAI channel layer; business side effects belong to downstream consumers of the outbound ETO.
/// </para>
/// </summary>
public class ContainerMarkerSetEvent
{
    /// <summary>The document whose container marker was just set (the former concrete-typed document).</summary>
    public Guid DocumentId { get; }

    /// <summary>The document's tenant, carried so the handler can publish the ETO without an extra load.</summary>
    public Guid? TenantId { get; }

    public ContainerMarkerSetEvent(Guid documentId, Guid? tenantId)
    {
        DocumentId = documentId;
        TenantId = tenantId;
    }
}
