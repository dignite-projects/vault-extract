using System;
using System.Globalization;
using System.Linq;
using Dignite.Abp.Notifications;
using Dignite.Abp.Notifications.SignalR;
using Dignite.Vault.Extract.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp.Localization;
using Xunit;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// What the framework sees (the definitions) and what the reader sees (the localized text). The second matters
/// because the message key and resource name are persisted inside each notification: a key that is missing in one
/// language shows the reader the raw key.
/// </summary>
public class DocumentNotificationDefinitions_Tests
    : VaultExtractApplicationTestBase<DocumentNotificationsTestModule>
{
    private static readonly string[] Names =
    {
        VaultExtractNotificationNames.DocumentNeedsReview,
        VaultExtractNotificationNames.DocumentFailed,
        VaultExtractNotificationNames.DocumentRejected,
        VaultExtractNotificationNames.DocumentReady
    };

    [Fact]
    public void All_four_notifications_are_defined_in_one_group_without_a_permission_gate()
    {
        var manager = GetRequiredService<INotificationDefinitionManager>();

        foreach (var name in Names)
        {
            var definition = manager.Get(name);
            definition.GroupName.ShouldBe(VaultExtractNotificationNames.GroupName);
            // Access to a document is the documents domain's own rule table, so no ABP permission gate.
            definition.PermissionName.ShouldBeNull();
        }

        manager.GetGroups().Select(g => g.Name).ShouldContain(VaultExtractNotificationNames.GroupName);
    }

    [Fact]
    public void Names_All_lists_exactly_the_defined_notifications()
    {
        // The default routing is applied to Names.All, so a notification missing from it would silently route to
        // nothing; and a stale entry would fail host startup (a rule for a notification no provider defines).
        VaultExtractNotificationNames.All.OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_notification_is_routed_to_SignalR_by_default()
    {
        // Routing, not the definition, carries the channel since Dignite.Abp.Notifications 10.0.0-rc.21. It is a
        // default the Application module ships so a host that runs the framework stateless (which fails at startup
        // for a notification that resolves to no channel) still starts without configuring anything.
        var routing = GetRequiredService<IOptions<NotificationRoutingOptions>>().Value;

        foreach (var name in Names)
        {
            routing.Notifications[name].ShouldBe(new[] { VaultExtractNotificationConsts.SignalRChannelName });
        }
    }

    [Fact]
    public void The_channel_name_is_the_one_the_SignalR_notifier_registers_under()
    {
        // Application spells the channel out instead of referencing the SignalR package (which would pull ASP.NET Core
        // into it). If the framework ever renames the channel, definitions would route to a notifier that does not
        // exist and live push would silently stop; this is the only thing that ties the two together.
        VaultExtractNotificationConsts.SignalRChannelName.ShouldBe(SignalRNotifier.ChannelName);
    }

    [Fact]
    public void The_payload_resource_name_is_the_real_resource()
    {
        // ResourceName is persisted in every notification; it has to keep resolving to this resource.
        var attribute = (LocalizationResourceNameAttribute)typeof(VaultExtractResource)
            .GetCustomAttributes(typeof(LocalizationResourceNameAttribute), inherit: false).Single();

        attribute.Name.ShouldBe(VaultExtractNotificationConsts.ResourceName);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Every_title_and_message_is_localized_in_every_supported_language(string culture)
    {
        var localizer = GetRequiredService<IStringLocalizer<VaultExtractResource>>();

        var keys = new[]
        {
            "Notification:Group:Documents",
            "Notification:NeedsReview", "Notification:NeedsReview:Message",
            "Notification:Failed", "Notification:Failed:Message",
            "Notification:Rejected", "Notification:Rejected:Message",
            "Notification:Ready", "Notification:Ready:Message"
        };

        using (CultureHelper.Use(new CultureInfo(culture)))
        {
            foreach (var key in keys)
            {
                var text = localizer[key];
                text.ResourceNotFound.ShouldBeFalse($"{culture} is missing '{key}'");
            }

            // The one parameterized message really takes its argument.
            localizer["Notification:Rejected:Message", "REASON"].Value.ShouldContain("REASON");
        }
    }
}
