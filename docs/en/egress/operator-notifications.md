# Operator notifications

The operator UI tells the person who uploaded a document when something happens to it, through a notification bell in the toolbar and an inbox page. This is the in-app counterpart of the [integration events](integration-events.md): the events are for downstream systems, the notifications are for the person at the keyboard. It is built on [Dignite.NotificationCenter](https://github.com/dignite-projects/abp-modules/tree/main/notifications).

Nothing here is part of the egress contract. No ETO was added and no event payload changed.

## What is sent, and to whom

Each notification goes to **the uploader only** (`Document.CreatorId`). A document with no owner, and any sub-document derived from a container, produces none: a container of 30 pages would otherwise notify 30 times for one upload.

| Notification | When the document... | Severity |
|---|---|---|
| Document needs review | enters `PendingReview`: a blocking review reason holds it back until an operator acts | Warn |
| Document processing failed | enters `Failed` because a pipeline run gave up | Error |
| Document rejected | enters `Failed` because an operator rejected it; carries the rejection reason. Not sent to the operator who rejected their own upload | Warn |
| Document is ready | enters `Ready`. A container gets one; its sub-documents get none | Success |

Clicking a notification opens the document. The text is a short localized sentence plus the document id: it carries no title and no document content. The one free-text value is the rejection reason, which the uploader can already read on the document.

People who review documents but did not upload them are **not** notified yet. Who may see a document is decided by the documents domain's access rules, not by an ABP permission, so reaching reviewers needs a per-user access check. That is separate work.

## Delivery

The lifecycle change is committed first; the notification is published afterwards, in its own transaction. A notification can therefore never roll a document's transition back or put a document job into its retry loop. If publishing fails it is logged and dropped, and a crash between the commit and the publish loses that one notification. This is deliberately weaker than the outbox-backed integration events, which are never lost.

Before publishing, the document is read again. If it has moved on to another state in the meantime, nothing is sent for the old one.

Delivery is the persisted inbox row plus a live push over SignalR (`/signalr-hubs/notifications`). The bell refreshes from the REST inbox (`/api/notification-center/notifications`), which is the source of truth: a push missed while the page was closed is still in the inbox. Email and device push channels are not enabled.

## Deploying

- **Database.** One host migration, `V680_AddNotificationCenter`, creates four tables: `NotifNotifications`, `NotifUserNotifications`, `NotifNotificationSubscriptions` and `NotifPushDevices`. It only adds tables. Run it with the normal `--migrate-database` step.
- **Connection.** The notification store uses the host's `Default` connection string. The distributed-event outbox stays on the host's own context.
- **SignalR.** The hub is served by the host itself and the browser connects to it directly, so `App:CorsOrigins` must contain the SPA origin. If a reverse proxy sits in front of the host it should pass WebSocket upgrades on `/signalr-hubs/`; without that the bell still works, through a slower fallback. See [Deployment: Reverse proxy](../deployment/deployment.md#reverse-proxy).
- **Subscriptions.** The notification module's "Subscriptions" settings tab is not enabled: every notification goes to an explicit recipient, which bypasses subscriptions, so its toggles would do nothing.
- **Upgrading from `abp-modules` 10.0.0-rc.22 or earlier.** The distribution job is now named `Dignite.Abp.Notifications.Distribute`, and jobs still queued under the old name (`Dignite.Abp.Notifications.NotificationDistributionJobArgs`) are not picked up after the upgrade. Vault Extract sends each notification to one explicit recipient, which is distributed inline (up to five recipients by default), so its own queue is normally empty. Check that `AbpBackgroundJobs` holds no row with the old `JobName` before you deploy; if it does, let the old version drain them first. No EF migration.

## Two ways to install the host

The Application module, the four definitions and the publishing code are the same in both. What differs is which packages the host installs, and so where the inbox, the channels and the SignalR hub live. Everything above describes the first.

| Host | Installs | Inbox and hub |
|---|---|---|
| **Monolith** (the Vault Extract host as shipped) | `Dignite.Abp.Notifications.SignalR` and `Dignite.NotificationCenter.Application`, `.HttpApi`, `.EntityFrameworkCore`. `Dignite.Abp.Notifications.Distribution` (the distributor and the delivery handler) arrives with `NotificationCenter.Application`; do not add it by hand | In this host, in its own database |
| **Split deployment** (one notification service shared by several products) | `Dignite.Abp.Notifications.Remote` and `Dignite.Abp.Notifications.DefinitionStore.EntityFrameworkCore`; no `Dignite.NotificationCenter.*`, no SignalR notifier | In the notification service. This host only publishes |

A host with both `Remote` and `Distribution` (which the Notification Center brings) fails at startup, and a host with neither has no `INotificationPublisher`.

In a split deployment:

- `INotificationPublisher` becomes `RemoteNotificationPublisher`. It checks that the notification is defined, serializes the payload, resolves the channels with this host's routing rules and publishes one `NotificationPublishRequestedEto` through this host's outbox. `Dignite.Vault.Extract.Application` does not change.
- The transaction that `DocumentNotificationDispatcher` opens therefore holds only the outbox write of that request. The inbox rows are written by the notification service when it handles the request. A notification is still best-effort, as described under Delivery.
- Map the `NotificationCenter` connection string to the notification service's database. The definition store saves this host's four notification definitions there at startup, which is how the service learns them.
- The SignalR hub and the inbox REST API (`/api/notification-center/...`) are provided by the notification service, not by this host. The proxy and `App:CorsOrigins` settings in the [deployment guide](../deployment/deployment.md#reverse-proxy) apply to wherever the hub is served.
- The routing stays here: `NotificationRoutingOptions` is read by this host, and the service uses the channels it receives as they are. The channel (`SignalR` by default) has to be hosted by the notification service.

The notification service has settings of its own (dynamic permission, feature and definition stores, the event inbox failure policy). They are described in the "Split deployment" section of the [Dignite.Abp.Notifications README](https://github.com/dignite-projects/abp-modules/tree/main/notifications), not repeated here.

## For a host that embeds Application

The handler and the four notification definitions are part of `Dignite.Vault.Extract.Application`, so any host that includes it already publishes. What happens to the notifications is the host's choice:

- **Nothing added:** Application routes the four notifications to the `SignalR` channel by default. With no notifier hosting that channel the framework logs a startup warning naming it, and with no inbox store nothing delivers them, so they are dropped.
- **Live push:** depend on `AbpNotificationsSignalRModule` (`Dignite.Abp.Notifications.SignalR`). That is what the default routing (`NotificationRoutingOptions`, set by the Application module) already targets. To change it, override in your host module, which is configured last and wins: `Configure<NotificationRoutingOptions>(o => o.ForNotification(VaultExtractNotificationNames.DocumentReady, "SignalR", "Email"))`, or `o.InboxOnly(...)` to keep a notification out of every external channel.
- **Inbox:** also depend on the `Dignite.NotificationCenter.*` modules, call `ConfigureNotificationCenter()` in the context you migrate from, and add a migration.
- **Inbox in a notification service:** depend on `AbpNotificationsRemoteModule` (`Dignite.Abp.Notifications.Remote`) and the definition store instead of the Notification Center, as described under [Two ways to install the host](#two-ways-to-install-the-host).

The notification names (`VaultExtract.Document.NeedsReview`, `.Failed`, `.Rejected`, `.Ready`) and the entity type `VaultExtract.Document` are stored with each notification and are frozen.
