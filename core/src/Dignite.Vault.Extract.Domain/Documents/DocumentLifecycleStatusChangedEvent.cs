using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Local domain event for document macro lifecycle status changes.
/// Published through AddLocalEvent inside <see cref="Document.TransitionLifecycle"/> in the same
/// transaction as the status change.
/// <para>
/// Valid consumption scenarios are limited to in-process hooks inside the Extract channel layer:
/// <list type="bullet">
///   <item>When transitioning to <c>Ready</c>, <c>DocumentReadyEventHandler</c> emits the
///   <c>DocumentReadyEto</c> outbound event.</item>
///   <item>When transitioning to <c>Failed</c> or <c>Ready</c>, push real-time notifications to an
///   operator UI SignalR/SSE hub.</item>
/// </list>
/// Business side effects such as user notifications, approval flows, or statistical aggregates belong
/// to downstream consumers. Subscribe to outbound ETOs such as <c>DocumentReadyEto</c> in their own
/// process instead of attaching to this local event.
/// </para>
/// </summary>
public class DocumentLifecycleStatusChangedEvent
{
    public Guid DocumentId { get; }
    public DocumentLifecycleStatus OldStatus { get; }
    public DocumentLifecycleStatus NewStatus { get; }

    public DocumentLifecycleStatusChangedEvent(
        Guid documentId,
        DocumentLifecycleStatus oldStatus,
        DocumentLifecycleStatus newStatus)
    {
        DocumentId = documentId;
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }
}
