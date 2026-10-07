namespace Dignite.Vault.Extract.Notifications;

public static class VaultExtractNotificationConsts
{
    /// <summary>
    /// Localization resource the message body is resolved against on the reading side. It is the
    /// <c>[LocalizationResourceName]</c> of <c>VaultExtractResource</c>, persisted inside each notification's JSON,
    /// so it is frozen like the other serialized string contracts.
    /// </summary>
    public const string ResourceName = "VaultExtract";

    /// <summary>
    /// The live-push channel, as the notification framework's SignalR notifier names it
    /// (<c>SignalRNotifier.ChannelName</c>). Spelled out here so Application does not reference the SignalR package,
    /// which would drag ASP.NET Core into it; <c>DocumentNotificationDefinitions_Tests</c> pins the two together.
    /// A host that wants the live push adds <c>AbpNotificationsSignalRModule</c>. Without it a notification is still
    /// stored in the inbox: a channel with no notifier registered is simply never called.
    /// </summary>
    public const string SignalRChannelName = "SignalR";
}
