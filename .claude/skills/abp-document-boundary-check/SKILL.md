---
name: abp-document-boundary-check
description: Verify that the Document aggregate remains a pure infrastructure aggregate, and scan for forbidden business fields such as contract amount, expiration date, invoice number, and similar domain-specific data. Use this after editing Document.cs, when adding a business module, or before closing a Slice.
---

# Document Aggregate Boundary Check

## Background And Invariants

`CLAUDE.md` defines a mandatory constraint:

> `Document` is a channel-layer infrastructure aggregate root. Its responsibility is limited to file storage, lifecycle state, text extraction output, AI classification output, and type-bound extracted field values.
>
> Do not add fields from downstream business modules to `Document`, such as contract amount, expiration date, counterparty name, invoice number, or similar business facts. Those fields belong to the downstream module's own aggregate root, and the downstream module should persist and query them after receiving `DocumentClassifiedEto` / `DocumentReadyEto`.

Decision rule: if a field only makes sense in a specific business scenario such as contracts, invoices, reimbursements, licenses, HR, medical claims, or insurance, it does not belong on `Document`.

## Fields Currently Allowed On Document.cs

The following fields are infrastructure-level fields and are allowed:

| Field Category | Field Names (Examples) |
| --- | --- |
| Multi-tenancy | `TenantId` |
| File storage | `FileOrigin` including `BlobName`, `OriginalFileName`, `ContentType`, and related file metadata |
| Cabinet / organization dimension | `CabinetId` |
| Lifecycle | `LifecycleStatus` |
| Text extraction | `Markdown` as the only text payload, `Title` as a Markdown-derived display snapshot, `Language`, `ExtractionMetadata` |
| Type-bound extracted field values | `ExtractedFieldValues` (`IReadOnlyCollection<DocumentExtractedField>`) |
| AI classification common metadata | `DocumentTypeId` as the internal immutable association, external wire-format `DocumentTypeCode`, `ClassificationConfidence` |
| Human review | `ReviewDisposition`, `ReviewReasons`, `RejectionReason` |

Notes:

- `DocumentTypeId` / `DocumentTypeCode` are generic document type identifiers, not business fields.
- `Markdown` is the only text payload. Do not introduce parallel text fields such as `ExtractedText`, `Summary`, or `RawText`.
- `ExtractionMetadata` stores provenance only, such as provider name, native payload blob manifest, and integrity or quality signals. Do not turn it into a business-field bag.

## Forbidden Patterns

Any field name or property meaning that only applies to a specific business scenario should move to that downstream business module's own aggregate root. Common red flags:

- Contract-related: `Amount`, `Currency`, `ContractNumber`, `EffectiveDate`, `ExpirationDate`, `ExpiryDate`, `Counterparty`, `CounterpartyName`, `SignedAt`, `SignedBy`, `ContractType`
- Invoice-related: `InvoiceNumber`, `InvoiceCode`, `TaxAmount`, `TaxRate`, `Buyer`, `Seller`, `IssuedAt`, `LineItems`
- Reimbursement / finance: `ReimbursementCategory`, `Reimbursable`, `CostCenter`, `Project`
- License / HR: `HolderName`, `IDNumber`, `IssuingAuthority`, `LicenseNumber`, `ValidFrom`, `ValidUntil`
- Any field containing industry-specific terms such as `PolicyNumber`, `PatientId`, or `ClaimAmount`
- Vectorization-related fields such as `HasEmbedding`, `EmbeddingStatus`, or similar. Vectorization belongs to downstream RAG systems and is outside the Dignite Vault Extract channel layer, so these fields are not allowed on `Document`.

## Trigger Scenarios

Run this check when any of the following is true:

1. The user edited `core/src/Dignite.Vault.Extract.Domain/Documents/Document.cs`.
2. The user added a property to `core/src/Dignite.Vault.Extract.Application.Contracts/Documents/DocumentDto.cs`. The DTO is the external projection of `Document`, so polluting the DTO is equivalent to polluting the aggregate.
3. The user says they want to "add field X to Document".
4. A new EF Core migration touches the `ExtractDocuments` table.
5. The user runs `/abp-document-boundary-check`.

## Execution Steps

1. **Read the current Document field inventory**

   Use Grep or rg to search property declarations in:

   - `core/src/Dignite.Vault.Extract.Domain/Documents/Document.cs`
   - `core/src/Dignite.Vault.Extract.Application.Contracts/Documents/DocumentDto.cs`
   - `core/src/Dignite.Vault.Extract.EntityFrameworkCore/EntityFrameworkCore/ExtractDbContextModelCreatingExtensions.cs` (the `builder.Entity<Document>` block)

2. **Classify ownership for each field**

   Compare each field against the allowed field categories and forbidden patterns above. For a suspicious field, do not immediately conclude that it is invalid. Answer these questions first:

   - Does the field's meaning hold across all document types? Allowed if yes; invalid if no.
   - Is it metadata, state, or orchestration data, or is it a business fact? Allowed for the former; invalid for the latter.
   - Is it written by channel-layer pipeline code, or by a downstream business module / business-specific extractor? Allowed for the former; invalid for the latter.

3. **If a violation is found, give a migration recommendation**

   Do not directly fix the code. Output:

   - The violating field name.
   - The business domain it belongs to, such as contracts, invoices, reimbursements, licenses, or HR. The downstream business consumer should implement it in its own repository and aggregate.
   - This exact conclusion in substance: "This field belongs to a downstream business aggregate root. The downstream consumer should subscribe to `DocumentClassifiedEto` / `DocumentReadyEto` and persist it in its own aggregate root. The Dignite Vault Extract `Document` aggregate is pure infrastructure and must not be polluted."
   - A reference to the relevant `CLAUDE.md` section for user review.

4. **If no violation is found, give a short confirmation**

   List the current fields and their ownership categories, confirm that all of them are within the allowed field categories, and report the checked change range, such as the latest `git diff Document.cs`.

## What Not To Do

- Do not proactively edit `Document.cs` to remove fields. That is a design decision and requires user confirmation.
- Do not misclassify existing generic metadata fields such as `ReviewDisposition`, `DocumentTypeId`, or `ExtractionMetadata` as violations.
- Do not apply the same rule to aggregate-internal infrastructure entities such as `DocumentExtractedField` or `DocumentPipelineRun`. This boundary check is about the `Document` aggregate root itself.

## References

- Root `CLAUDE.md` -> "Field architecture", "Markdown-first", and "OUT of scope"
- `.claude/rules/ddd-patterns.md` -> aggregate design and DDD invariants
- `.claude/rules/dependency-rules.md` -> cross-layer and cross-module dependency direction
