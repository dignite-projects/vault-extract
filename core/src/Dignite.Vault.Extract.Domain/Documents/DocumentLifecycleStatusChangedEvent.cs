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
///   <item>When transitioning to <c>PendingReview</c>, <c>Failed</c> or <c>Ready</c>,
///   <c>DocumentLifecycleNotificationHandler</c> (Application, #680) notifies the uploader in the operator UI. This is the only trigger for those notifications: no ETO
///   exists for <c>PendingReview</c> or <c>Failed</c>.</item>
/// </list>
/// Notifying the operator inside the channel's own UI is in scope. Business side effects such as
/// approval flows, notifying other people about a document, or statistical aggregates belong to
/// downstream consumers. Subscribe to outbound ETOs such as <c>DocumentReadyEto</c> in their own
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
