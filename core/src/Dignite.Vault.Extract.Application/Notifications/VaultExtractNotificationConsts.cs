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
    /// (<c>SignalRNotifier.ChannelName</c>). It is the channel of the default routing this assembly ships
    /// (<c>NotificationRoutingOptions</c>, set in <c>VaultExtractApplicationModule</c>); a host overrides it there.
    /// Spelled out here so Application does not reference the SignalR package, which would drag ASP.NET Core into
    /// it; <c>DocumentNotificationDefinitions_Tests</c> pins the two together. A host that wants the live push adds
    /// <c>AbpNotificationsSignalRModule</c>. Without it, startup logs a warning naming the channel, and a
    /// notification is still stored in the inbox if the host has one.
    /// </summary>
    public const string SignalRChannelName = "SignalR";
}
