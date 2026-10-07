using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Localization;
using Volo.Abp.Localization;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// Defines the four document notifications. Deliberately without <c>RequirePermission</c>: who may see a document
/// is decided by the documents domain's access rules, not by an ABP permission, so a definition-level gate could not
/// express it. Eligibility is instead the recipient choice itself (the uploader; see
/// <see cref="DocumentNotificationPlanner"/>). Delivery channels are not part of a definition either: since
/// Dignite.Abp.Notifications 10.0.0-rc.21 they are routing (<c>NotificationRoutingOptions</c>).
/// </summary>
public class DocumentNotificationDefinitionProvider : NotificationDefinitionProvider
{
    public override void Define(INotificationDefinitionContext context)
    {
        var group = context.AddGroup(
            VaultExtractNotificationNames.GroupName,
            LocalizableString.Create<VaultExtractResource>("Notification:Group:Documents"));

        // Which channels carry these is routing, not definition: see VaultExtractApplicationModule (default) and
        // NotificationRoutingOptions (the host overrides).
        group.AddNotification(
            VaultExtractNotificationNames.DocumentNeedsReview,
            LocalizableString.Create<VaultExtractResource>("Notification:NeedsReview"));

        group.AddNotification(
            VaultExtractNotificationNames.DocumentFailed,
            LocalizableString.Create<VaultExtractResource>("Notification:Failed"));

        group.AddNotification(
            VaultExtractNotificationNames.DocumentRejected,
            LocalizableString.Create<VaultExtractResource>("Notification:Rejected"));

        group.AddNotification(
            VaultExtractNotificationNames.DocumentReady,
            LocalizableString.Create<VaultExtractResource>("Notification:Ready"));
    }
}
