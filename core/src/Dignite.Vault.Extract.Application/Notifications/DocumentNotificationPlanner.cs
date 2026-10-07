using System;
using System.Collections.Generic;
using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Documents;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// The facts about a document the notification decision reads, copied out of the aggregate so they can be carried
/// past the unit of work that produced them.
/// </summary>
public sealed record DocumentNotificationSnapshot(
    Guid DocumentId,
    Guid? TenantId,
    Guid? OwnerId,
    DocumentLifecycleStatus Status,
    Guid? OriginDocumentId,
    DocumentReviewDisposition ReviewDisposition,
    string? RejectionReason)
{
    public static DocumentNotificationSnapshot From(Document document) => new(
        document.Id,
        document.TenantId,
        document.CreatorId,
        document.LifecycleStatus,
        document.OriginDocumentId,
        document.ReviewDisposition,
        document.RejectionReason);
}

/// <summary>One notification to publish: what, to whom, and about which document.</summary>
public sealed record DocumentNotification(
    string Name,
    NotificationData Data,
    NotificationSeverity Severity,
    Guid RecipientId,
    NotificationEntityIdentifier Entity);

/// <summary>
/// Decides whether a lifecycle transition becomes a notification, and which. A pure function of the document's
/// facts, so the rules live in one place and are testable without a host.
/// <para>
/// <b>Recipient: the uploader only</b> (<c>Document.CreatorId</c>, #680). The documents domain authorizes through
/// <c>DocumentAccessRule</c>, not ABP permissions, so there is no cheap, correct answer to "who else may see this
/// document"; fanning out to reviewers needs a per-user access check and is a separate piece of work. A document with
/// no owner publishes nothing.
/// </para>
/// <para>
/// <b>Payload is thin</b>: a localized sentence and the document id (as the entity identity). No title, so nothing
/// derived from document content crosses into the notification store. The one free-text value is the operator's
/// rejection reason, which the uploader can already read on the document itself.
/// </para>
/// </summary>
public static class DocumentNotificationPlanner
{
    public static bool IsNotifiable(DocumentLifecycleStatus status) =>
        status is DocumentLifecycleStatus.PendingReview
            or DocumentLifecycleStatus.Failed
            or DocumentLifecycleStatus.Ready;

    /// <param name="document">The document as it stands once the transition is committed.</param>
    /// <param name="actorId">
    /// Who caused the transition, or <c>null</c> for the pipeline. Used only to keep an uploader from being notified
    /// of their own rejection.
    /// </param>
    public static DocumentNotification? Plan(DocumentNotificationSnapshot document, Guid? actorId)
    {
        if (document.OwnerId is not { } ownerId)
        {
            return null;
        }

        // A sub-document is spawned from its parent, not uploaded: a container of N children would otherwise
        // produce N notifications for one upload. The parent (container or concrete) is the one the uploader knows.
        if (document.OriginDocumentId.HasValue)
        {
            return null;
        }

        var entity = new NotificationEntityIdentifier(
            VaultExtractNotificationNames.DocumentEntityTypeName,
            document.DocumentId.ToString());

        switch (document.Status)
        {
            case DocumentLifecycleStatus.PendingReview:
                return Build(
                    VaultExtractNotificationNames.DocumentNeedsReview, "Notification:NeedsReview:Message",
                    NotificationSeverity.Warn, ownerId, entity);

            case DocumentLifecycleStatus.Ready:
                return Build(
                    VaultExtractNotificationNames.DocumentReady, "Notification:Ready:Message",
                    NotificationSeverity.Success, ownerId, entity);

            case DocumentLifecycleStatus.Failed
                when document.ReviewDisposition == DocumentReviewDisposition.Rejected:
                // Failed has two causes: a pipeline that gave up, and an operator's rejection (the coarse
                // "unavailable" appearance of RejectReview). Only the second carries a reason, and the person who
                // rejected needs no telling.
                if (actorId == ownerId)
                {
                    return null;
                }

                return Build(
                    VaultExtractNotificationNames.DocumentRejected, "Notification:Rejected:Message",
                    NotificationSeverity.Warn, ownerId, entity,
                    document.RejectionReason);

            case DocumentLifecycleStatus.Failed:
                return Build(
                    VaultExtractNotificationNames.DocumentFailed, "Notification:Failed:Message",
                    NotificationSeverity.Error, ownerId, entity);

            default:
                return null;
        }
    }

    private static DocumentNotification Build(
        string name,
        string messageKey,
        NotificationSeverity severity,
        Guid recipientId,
        NotificationEntityIdentifier entity,
        string? argument = null)
    {
        var data = new LocalizableMessageNotificationData(VaultExtractNotificationConsts.ResourceName, messageKey);

        if (argument != null)
        {
            // Positional ({0}) on every reader: the server-side and Angular renderers both apply the values in order.
            data.Arguments = new Dictionary<string, object> { ["reason"] = argument };
        }

        return new DocumentNotification(name, data, severity, recipientId, entity);
    }
}
