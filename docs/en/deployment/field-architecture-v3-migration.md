```json
//[doc-seo]
{
    "Description": "Runbook for moving a Dignite Vault Extract 0.3.x deployment's field values to field architecture v3 before upgrading to 0.5.0: which build, steps, verification and rollback."
}
```

# Field Architecture v2 → v3 Migration

Runbook for upgrading a deployment across the field-storage change of the `0.5.0` line
([#558](https://github.com/dignite-projects/vault-extract/issues/558),
[#561](https://github.com/dignite-projects/vault-extract/issues/561)). Read it before upgrading a
deployment that already holds extracted field values — a package upgrade alone does **not** migrate
them, and nothing in the tooling reminds you.

> **Which build to run this on.** The data migrator exists only in **`0.5.0-preview.1` and
> `0.5.0-preview.2`**, the two releases in which the v2 and v3 storage coexist. They were published
> to GitHub Packages, not to NuGet.org. From `0.5.0-preview.4` on — `0.5.0` included — the v2 tables
> are dropped ([#593](https://github.com/dignite-projects/vault-extract/issues/593)) and the migrator
> is gone. So: upgrade the 0.3.x deployment to one of those two previews, complete this runbook and
> its verification, and only then move to `0.5.0` or later. Skipping it loses every field value while
> the upgrade reports success.

## Does This Apply to You?

| Situation | Applies |
|---|---|
| Upgrading a deployment from `0.3.x` or earlier that has extracted field values | **Yes** — run every step, on `0.5.0-preview.1` or `0.5.0-preview.2` |
| Fresh install of `0.5.0`+ | No — there is no v2 data |
| Upgrading a deployment with document types but no extracted values yet | Yes, but only field definitions move |

## What Changes

| | v2 (≤ `0.3.x`) | v3 (`0.5.0` line) |
|---|---|---|
| Field definitions | `VaultFieldDefinitions` | `VaultFields` |
| Field values | `VaultDocumentExtractedFields` (typed child rows) | `VaultDocuments.FlexFields` (JSON bag, authoritative) |
| Query index | the composite indexes on the child table | `VaultDocumentFlexFieldIndexes` (derived, rebuildable) |
| Validation warnings | `VaultDocumentFieldValidationWarnings` | unchanged rows; the FK re-points at `VaultFields` |

**This is expand-then-contract.** The migration is additive: `VaultFieldDefinitions` and
`VaultDocumentExtractedFields` keep every row and are **not** dropped. That is deliberate — while
they hold their rows they are the rollback path, and a stronger one than any tag, because the data is
still there. Dropping them was a separate, later change
([#593](https://github.com/dignite-projects/vault-extract/issues/593), shipped in `0.5.0-preview.4`).

## What Runs the Migration

`FieldArchitectureV3Migrator` — an `ITransientDependency` in the **Application layer**, not in a
host's data folder. It lives in the module because a downstream can host the same EF model in its own
`DbContext` and own its own migrations; a host-local script would migrate one deployment and silently
leave the other on the v2 layout.

**Nothing calls it automatically.**

- **This repository's host** calls it from `VaultExtractHostDbMigrationService`, under
  `--migrate-database` only. That path already iterates the host layer plus every tenant, and calls
  `RecomputeFingerprintsAsync` for each.
- **A downstream hosting the EF model in its own `DbContext`** must call it from its own migration
  path. The `vault` Pro edition does this through its own `IVaultFieldArchitectureV3Migrator`
  wrapper, invoked from its `DbMigrator`.

Three properties worth knowing before you run it:

- **Additive** — nothing is deleted.
- **Idempotent and resumable** — a second run migrates nothing new and will not overwrite a bag an
  operator has since edited. The derived-index rebuild runs *unconditionally*, including on a
  no-op run: an "already migrated, skip" shortcut is exactly what would strand a run that failed
  partway through the rebuild while reporting success.
- **One layer per call** — it does not pierce ABP's `IMultiTenant` filter, so iterating host + tenants
  is the caller's job. The result reports which layer it actually handled, so a caller can assert it
  matches the layer requested rather than trusting the scope took.

## Steps

### 1. Back up the database

Not optional. Once the v2 tables are dropped (`0.5.0-preview.4` and later) this is the only rollback, and the
verification in step 5 is what tells you whether you still need it.

### 2. Measure

Small deployments run single-shot. Read the counts anyway — it takes seconds and tells you which
plan you are on.

```sql
SELECT COUNT(*) AS Documents            FROM VaultDocuments;
SELECT COUNT(*) AS ExtractedFieldRows   FROM VaultDocumentExtractedFields;
SELECT COUNT(*) AS FieldDefinitions     FROM VaultFieldDefinitions;
SELECT COUNT(*) AS DocumentTypes        FROM VaultDocumentTypes;
```

**Threshold:** single-shot up to roughly **100k** rows in `VaultDocumentExtractedFields`. Beyond
that, batch the bag-write pass by document id and checkpoint — a single transaction rewriting every
document row is a long lock on the one table the whole product reads.

### 3. Apply the schema migration

Additive: it adds `VaultFields`, the `FlexFields` column on `VaultDocuments`, and
`VaultDocumentFlexFieldIndexes`, and re-points the validation-warning FK. It drops nothing.

```bash
dotnet ef database update
```

> The `FlexFields` column must default to `{}`, not `''`. An empty string is not valid JSON and every
> pre-existing document becomes unreadable **at read time**, long after the migration reported
> success. The shipped migration gets this right; if you hand-write one for your own `DbContext`,
> check it.

### 4. Run the data migration

For this repository's host:

```bash
dotnet run --project host/src -- --migrate-database
```

This applies the schema migration, then migrates the host layer and every tenant layer, then
recomputes fingerprints per layer. A downstream with its own `DbContext` runs its own equivalent
(`vault`: its `DbMigrator`).

**Fingerprint recomputation is required, not optional.** `Document.FieldFingerprint` is a stored
SHA-256 that drives duplicate detection ([#411](https://github.com/dignite-projects/vault-extract/issues/411)),
and v3 hashes it from the value bag where v2 hashed it from value rows in `Order` sequence.
Duplicates are decided by string equality of that stored hash, so leaving it unrecomputed silently
partitions the corpus: every document keeps its v2 fingerprint until it is re-extracted, and then
stops matching the ones that were not. It runs automatically in the host path above, immediately
after that layer's bags exist.

### 5. Verify

Every query below was run against a real migrated database before being written down, which is how
the collation cast in 5a and the corrected arithmetic in 5c got here. 5a is the one that matters.

**5a — every v2 value survived into a bag.** Compare per document + field name.

```sql
-- Rows returned = mismatches. Zero rows is the pass condition.
WITH V2 AS (
    SELECT  ef.DocumentId,
            fd.Name           AS FieldName,
            ef.[Order],
            COALESCE(
                ef.TextValue,
                ef.LongTextValue,
                CONVERT(nvarchar(max), ef.NumberValue),
                CONVERT(nvarchar(max), ef.DateValue,     126),
                CONVERT(nvarchar(max), ef.DateTimeValue, 126),
                CASE ef.BooleanValue WHEN 1 THEN 'true' WHEN 0 THEN 'false' END
            ) AS V2Value
    FROM VaultDocumentExtractedFields ef
    JOIN VaultFieldDefinitions fd ON fd.Id = ef.FieldDefinitionId
),
V3 AS (
    SELECT  d.Id AS DocumentId,
            bag.[key] COLLATE DATABASE_DEFAULT AS FieldName,
            bag.[value] AS RawValue
    FROM VaultDocuments d
    CROSS APPLY OPENJSON(d.FlexFields) AS bag
    WHERE ISJSON(d.FlexFields) = 1
)
SELECT V2.DocumentId, V2.FieldName, V2.[Order], V2.V2Value
FROM V2
LEFT JOIN V3 ON V3.DocumentId = V2.DocumentId AND V3.FieldName = V2.FieldName
WHERE V3.DocumentId IS NULL
ORDER BY V2.DocumentId, V2.FieldName, V2.[Order];
```

> The `COLLATE DATABASE_DEFAULT` is not optional. `OPENJSON` returns its `key` column as
> `Latin1_General_BIN2`, while `Name` carries the database collation, and joining them without a cast
> fails outright:
> `Msg 468 — Cannot resolve the collation conflict between "SQL_Latin1_General_CP1_CI_AS" and "Latin1_General_BIN2"`.
> The `ISJSON` guard is for the same class of surprise: one row with a non-JSON `FlexFields` value
> would otherwise abort the whole query rather than being reported.

> **Use `OPENJSON`, never `JSON_VALUE`, for these comparisons.** `JSON_VALUE` returns `nvarchar(4000)`
> and yields `NULL` for anything longer, so a long `LongText` value reads as empty and looks exactly
> like data loss. This produced a false alarm during the dev-database run; re-read through `OPENJSON`
> the value was intact.

**5b — definition counts match, per layer.**

```sql
SELECT 'v2' AS Layer, TenantId, COUNT(*) AS Cnt,
       SUM(CASE WHEN IsDeleted = 1 THEN 1 ELSE 0 END) AS Deleted
FROM VaultFieldDefinitions GROUP BY TenantId
UNION ALL
SELECT 'v3', TenantId, COUNT(*), SUM(CASE WHEN IsDeleted = 1 THEN 1 ELSE 0 END)
FROM VaultFields GROUP BY TenantId
ORDER BY TenantId, Layer;
```

Ids are preserved, so these should agree — including soft-deleted definitions, which are migrated too
(the validation-warning FK and the derived index both key on the id). Watch the rows where `TenantId`
is **not** null: a host-only migration pass leaves the tenant layers entirely on v2 and this is where
that shows.

The counts will not always match exactly, and a bare count cannot tell a benign difference from a
lost row. Once v3 owns writes, a field created after the migration exists in `VaultFields` only.
Compare by id instead — that is what turns a number into an answer:

```sql
-- In v2, missing from v3. Anything here with ValueRows > 0 is the serious case:
-- its values are in the bag with no definition backing them, which the field
-- architecture forbids (every bag key must have a persisted definition).
SELECT fd.Id, fd.Name, fd.TenantId, fd.IsDeleted,
       (SELECT COUNT(*) FROM VaultDocumentExtractedFields ef WHERE ef.FieldDefinitionId = fd.Id) AS ValueRows
FROM VaultFieldDefinitions fd
WHERE NOT EXISTS (SELECT 1 FROM VaultFields f WHERE f.Id = fd.Id);

-- In v3, absent from v2: expected — fields created after the cutover.
SELECT f.Id, f.Name, f.TenantId, f.FieldTypeName, f.CreationTime
FROM VaultFields f
WHERE NOT EXISTS (SELECT 1 FROM VaultFieldDefinitions fd WHERE fd.Id = f.Id);
```

**5c — the derived index is populated.**

```sql
SELECT f.FieldTypeName, COUNT(*) AS IndexRows
FROM VaultDocumentFlexFieldIndexes i
JOIN VaultFields f ON f.Id = i.FieldId
GROUP BY f.FieldTypeName ORDER BY IndexRows DESC;
```

Two things to read off it. **No `CKEditor` row may appear**: that field type's `IndexValueType` is
null, so long text never enters the index — the same treatment v2's `LongTextValue` column got, which
was excluded from every composite index. And the total must equal the number of bag entries whose
field type *is* indexable:

```sql
SELECT COUNT(*) AS BagEntries
FROM VaultDocuments d CROSS APPLY OPENJSON(d.FlexFields) bag
WHERE ISJSON(d.FlexFields) = 1;
-- IndexRows = BagEntries - (bag entries on CKEditor fields)
```

Compare against **bag entries**, not against the `VaultDocumentExtractedFields` count. Those two
agree only in the moment right after the migration; from the cutover onward v3 owns writes, so the
bag grows while the v2 table stays frozen, and "index rows = v2 rows − long-text rows" stops holding.

A count of **zero** means the rebuild did not run; re-run the migration (it is idempotent, and the
rebuild is unconditional).

**5d — duplicate detection did not shift.**

```sql
-- Run before and after; the two numbers must be identical.
SELECT COUNT(*) FROM VaultDocuments WHERE ReviewReasons & 8 = 8;   -- 8 = DuplicateSuspected
```

A change here means the fingerprint recomputation changed which documents collide, which is the
failure this step exists to catch.

> This one only works as a **pair** of readings. Record the number before step 4; if the migration
> has already run and nobody took it, the "after" figure on its own proves nothing, and 5a becomes
> the check carrying the weight. Take the reading first — it costs one query.

Finally, in the UI: open a handful of documents of different types and confirm their fields render
with the same values as before, and that a field-value filter on the document list returns the same
result set.

### 6. Stop

Do **not** drop `VaultFieldDefinitions` or `VaultDocumentExtractedFields` by hand. They are the
rollback path until you upgrade past `0.5.0-preview.2`; the migration of
[#593](https://github.com/dignite-projects/vault-extract/issues/593) (`0.5.0-preview.4`) drops them.

## Rollback

| Point of failure | Recovery |
|---|---|
| Schema migration failed | Restore the backup; nothing has been written |
| Data migration failed partway | Re-run it — additive and resumable, so it picks up where it stopped |
| Verification failed | Revert the application to the previous version. The v2 tables are intact and authoritative; no data was destroyed |
| After the v2 tables are dropped (`0.5.0-preview.4` and later) | Restore from backup. This is why the drop is a separate release |

## Note for Downstream Consumers

If you host the Vault Extract EF model in your own `DbContext` and generate your own migrations,
there is a sequence that loses data silently and nothing prevents it:

1. You skip the coexistence release and upgrade straight to one where the v2 entities are gone.
2. `dotnet ef migrations add` diffs the model and emits `DROP TABLE VaultDocumentExtractedFields`
   **and** `CREATE TABLE VaultFields` in one migration.
3. `ef database update` drops the v2 tables.
4. `FieldArchitectureV3Migrator` reads those tables. It now has no window to run — the source rows
   are gone, the upgrade reports success, and every field value is lost.

So: upgrade to a release where v2 and v3 **coexist** (`0.5.0-preview.1` or `0.5.0-preview.2`), generate an additive-only migration,
apply it, invoke the migrator from your own migration path, and complete the verification above
*before* moving to any release that drops the v2 tables.

## See Also

- [Deployment](deployment.md)
- [Document Types and Fields](../documents/document-types.md)
- [CHANGELOG](https://github.com/dignite-projects/vault-extract/blob/main/CHANGELOG.md) — `0.5.0-preview.1` to `0.5.0-preview.4`
