namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// The names this exit publishes under. Every value here is persisted (<c>Notification.NotificationName</c>,
/// <c>EntityTypeName</c>) and keys UI renderers and subscriptions, so each one is a frozen wire string like the
/// error codes: rename the constant, never the value.
/// </summary>
public static class VaultExtractNotificationNames
{
    public const string GroupName = "VaultExtract.Documents";

    /// <summary>A blocking review reason holds the document back; it waits on an operator.</summary>
    public const string DocumentNeedsReview = "VaultExtract.Document.NeedsReview";

    /// <summary>A critical pipeline failed for good (retries exhausted).</summary>
    public const string DocumentFailed = "VaultExtract.Document.Failed";

    /// <summary>An operator, not the recipient, rejected the document.</summary>
    public const string DocumentRejected = "VaultExtract.Document.Rejected";

    public const string DocumentReady = "VaultExtract.Document.Ready";

    /// <summary>
    /// <c>EntityTypeName</c> of the document a notification is about. The Angular UI keys its click-through
    /// resolver on it.
    /// </summary>
    public const string DocumentEntityTypeName = "VaultExtract.Document";
}
