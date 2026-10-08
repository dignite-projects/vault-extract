# GDPR user-data erasure

When ABP's GDPR module reports that a user asked for their data to be deleted, Vault Extract erases that user's name from every document they uploaded. Documents themselves are kept. Decision record: [#698](https://github.com/dignite-projects/vault-extract/issues/698).

## What is erased, and what is not

Every uploaded document stores a snapshot of the uploader's display name, so the document can still say who uploaded it after the account is gone. On an erasure request that snapshot (`FileOrigin.UploadedByUserName`, shown as the uploader on the document detail page) is replaced by the fixed text `[deleted user]`.

| Kept | Why |
|---|---|
| The documents, their Markdown, fields and original files | They are the tenant's records, not the requester's account data. Deleting them would also tell every downstream consumer that the document was permanently deleted. |
| `CreatorId`, `LastModifierId`, `DeleterId` | These are ids, not names. ABP's Identity Pro handler, which the GDPR module relies on, anonymizes the account and deletes it (a soft delete unless configured otherwise), so an id no longer points at a person. Vault Extract cannot see that handler: it is the publishing host's job to anonymize the account. Document ownership and duplicate detection still need `CreatorId`. |

One consequence to know about: once the account is gone, nobody holds the owner arm on those documents any more, so they stay reachable only through the role-level permissions (`Documents.ReadAll` and its siblings) or a per-type grant, never through ownership. A host that publishes the event but keeps the account active leaves that user the owner of their documents. See [per-document-type permissions](../configuration/document-type-permissions.md).

Not handled here, because it belongs to other modules: the notification inbox (Dignite.NotificationCenter), ABP's audit log, permission grants and tokens, and any copy a downstream consumer already received. Operator notifications are still addressed to `CreatorId`, so a later state change on one of the user's documents can create a new inbox row for the erased user's id; it carries no name and nobody can read it. Personal data inside a document's own content is also out of scope.

## When it runs

The handler subscribes to `GdprUserDataDeletionRequestedEto` from the `Volo.Abp.Gdpr.Abstractions` package. **That event is published by ABP's GDPR module, which is a commercial module (ABP Team license or higher).** Vault Extract does not ship it, so on a host without that module nothing ever publishes the event and the handler stays idle. Hosts that run the module need no configuration.

The event carries only the user's id. Vault Extract sweeps the Host and every tenant, inactive tenants included, because one account can be a member of several tenants. Each layer is erased in its own unit of work. Documents in the recycle bin are erased too.

> The tenant sweep can be dropped once ABP carries the tenant on the event. ABP's 10.7.0-rc.2 Pro release entry ([PR 22698](https://abp.io/pro-releases/pr/22698)) says published events now do; confirm that against the final package before removing it. Until then the cost is one extra statement per tenant.

## Delivery and failure

Delivery is at-least-once, and the erasure is idempotent: a redelivered event matches no rows and changes nothing. An unknown user id is a no-op.

If one tenant fails (for example its database is unreachable), the handler **logs an error naming that tenant and carries on with the others; it does not throw.** The event bus retries a failed event as a whole, which would re-run the other handlers of the same event, Identity's account erasure among them, and under ABP's `Retry` inbox failure policy it also stops the inbox queue. The price is that **nothing retries a failed tenant on its own**, and there is no built-in command to re-run the erasure: once the cause is fixed, publish the deletion event for that user again from your host (the handler is idempotent). Look for this message:

```
GDPR erasure of user <id>: the uploader name was NOT erased in tenant <id>. Publish the deletion event again to retry; it is idempotent.
```

Two failures are deliberately not swallowed and fail the event: cancellation (every remaining tenant would fail the same way), and a failure to list the tenants (nothing has been changed yet, and the tenant store being down fails the other handlers too).

A successful erasure logs, per layer, `erased the uploader name on N document(s)`.

## Limits

- **Documents are found by `CreatorId`.** ABP does not record the creator when the document's tenant differs from the signed-in user's, so a Host user who uploads while acting inside a tenant leaves a document that has their name but no creator. That name is not reached. Closing the gap means persisting the uploader's id next to the name, which is a schema change and its own Issue.
- **A save in flight when the erasure runs fails.** The erasure changes the document's concurrency stamp, so a document a job or an operator loaded before the erasure and saves after it fails with a concurrency error instead of writing the old name back.

## Database

No migration. The erasure is one `UPDATE` per layer on the existing `Documents` table. It changes `UploadedByUserName` and the concurrency stamp, and nothing else: `LastModificationTime` and `LastModifierId` stay as they were, so the trace of anyone else who edited a document survives.
