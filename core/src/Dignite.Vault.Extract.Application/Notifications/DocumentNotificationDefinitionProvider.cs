using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Localization;
using Volo.Abp.Localization;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// Defines the four document notifications. Deliberately without <c>RequirePermission</c>: who may see a document
/// is decided by the documents domain's access rules, not by an ABP permission, so a definition-level gate could not
/// express it. Eligibility is instead the recipient choice itself (the uploader; see
/// <see cref="DocumentNotificationPlanner"/>).
/// </summary>
public class DocumentNotificationDefinitionProvider : NotificationDefinitionProvider
{
    public override void Define(INotificationDefinitionContext context)
    {
        var group = context.AddGroup(
            VaultExtractNotificationNames.GroupName,
            LocalizableString.Create<VaultExtractResource>("Notification:Group:Documents"));

        // SignalR for the live bell; the persisted inbox row is written regardless of channels.
        group.AddNotification(
                VaultExtractNotificationNames.DocumentNeedsReview,
                LocalizableString.Create<VaultExtractResource>("Notification:NeedsReview"))
            .UseChannels(VaultExtractNotificationConsts.SignalRChannelName);

        group.AddNotification(
                VaultExtractNotificationNames.DocumentFailed,
                LocalizableString.Create<VaultExtractResource>("Notification:Failed"))
            .UseChannels(VaultExtractNotificationConsts.SignalRChannelName);

        group.AddNotification(
                VaultExtractNotificationNames.DocumentRejected,
                LocalizableString.Create<VaultExtractResource>("Notification:Rejected"))
            .UseChannels(VaultExtractNotificationConsts.SignalRChannelName);

        group.AddNotification(
                VaultExtractNotificationNames.DocumentReady,
                LocalizableString.Create<VaultExtractResource>("Notification:Ready"))
            .UseChannels(VaultExtractNotificationConsts.SignalRChannelName);
    }
}
