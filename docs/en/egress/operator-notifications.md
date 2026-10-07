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
- **SignalR.** The hub is served by the host itself. The browser connects to it directly, so a reverse proxy must pass WebSocket upgrades on `/signalr-hubs/` and `App:CorsOrigins` must contain the SPA origin.
- **Subscriptions.** The notification module's "Subscriptions" settings tab is not enabled: every notification goes to an explicit recipient, which bypasses subscriptions, so its toggles would do nothing.

## For a host that embeds Application

The handler and the four notification definitions are part of `Dignite.Vault.Extract.Application`, so any host that includes it already publishes. What happens to the notifications is the host's choice:

- **Nothing added:** they go to the framework's no-op store with no channel, and are dropped.
- **Live push:** depend on `AbpNotificationsSignalRModule` (`Dignite.Abp.Notifications.SignalR`). The definitions route to the channel named `SignalR`; without its notifier registered the channel is simply never called.
- **Inbox:** also depend on the `Dignite.NotificationCenter.*` modules, call `ConfigureNotificationCenter()` in the context you migrate from, and add a migration.

The notification names (`VaultExtract.Document.NeedsReview`, `.Failed`, `.Rejected`, `.Ready`) and the entity type `VaultExtract.Document` are stored with each notification and are frozen.
