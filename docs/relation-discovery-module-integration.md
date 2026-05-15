# Integrating a Business Module with Relation Discovery

You're writing a Paperbase business module — invoice, purchase order, employee record,
medical case, patent application, whatever — and you want the documents it owns to
participate in Paperbase's automatic relation discovery, so the in-app graph view and
the chat agent see the right connections.

This page is the integration contract. It covers what the core layer guarantees,
what your module must guarantee, and how cross-module interoperability works **without**
any central vocabulary maintained by the core.

For operators (how to read telemetry, tune trigger delay) see
[relation-discovery.md](relation-discovery.md). For the high-level architecture
rationale see Issue #115.

---

## What you get when you integrate

Your module's typed records (e.g. `Contract`, `Invoice`) become **first-class participants**
in the relation graph:

- RelationDiscovery automatically connects documents that share an identifier value or
  multi-field signature emitted by your provider.
- Manual relations a user draws in the UI go through the same `DocumentRelation`
  aggregate.
- Chat agent's `search_paperbase_documents` and `get_document_relations` tools surface
  the relations your provider produced.

You opt in by implementing **one or both** of two open contracts. No core code change.
No registration with a central enum.

---

## Two integration points

| Contract | When to use | Example |
|---|---|---|
| `IDocumentIdentifierProvider` | Your module has a **single field** whose value uniquely identifies a business entity (contract number, invoice number, PO number). | Two documents both hold contract number `HT-2024-001` → they're related. |
| `IDocumentEntitySignatureProvider` | Your module needs **multiple fields combined** to identify a business entity (parties + year, vendor + PO + amount). Single-field matching would be ambiguous. | A main contract and its supplement share `(PartyA, PartyB, signing year)` but have **different** contract numbers. |

Both contracts live in `Dignite.Paperbase.Abstractions` (assembly
`Dignite.Paperbase.Abstractions.dll`). Reference it from your module's Domain project
and you're done with the dependency side.

---

## Identifier provider — step by step

### 1. Implement the interface

```csharp
public class InvoiceIdentifierProvider : IDocumentIdentifierProvider, ITransientDependency
{
    // String constants identifying the types this provider emits and looks up.
    // Module-private types: use a "<ModuleName>.<TypeName>" prefix to avoid collisions
    // with other modules. Cross-module types (you want to interoperate with another
    // module's data — see "Cross-module interoperability" below): reuse the OTHER
    // module's public constant.
    public const string InvoiceNumberTypeId = "Invoices.InvoiceNumber";

    public IReadOnlyCollection<string> SupportedIdentifierTypes { get; } = new[]
    {
        InvoiceNumberTypeId,
        // To make this module's invoices match contracts that share the contract number:
        Contracts.ContractIdentifierProvider.ContractNumberTypeId,
    };

    private readonly IInvoiceRepository _invoiceRepository;
    public InvoiceIdentifierProvider(IInvoiceRepository invoiceRepository) { /* … */ }

    public async Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(
        Guid documentId, CancellationToken ct = default)
    {
        var invoice = await _invoiceRepository.FindByDocumentIdAsync(documentId);
        if (invoice == null) return Array.Empty<DocumentIdentifierEntry>();

        var entries = new List<DocumentIdentifierEntry>();
        AddIfPresent(entries, InvoiceNumberTypeId, invoice.InvoiceNumber);
        AddIfPresent(entries,
            Contracts.ContractIdentifierProvider.ContractNumberTypeId,
            invoice.ReferencedContractNumber);
        return entries;
    }

    public async Task<IReadOnlyList<Guid>> FindDocumentsAsync(
        string identifierType, string normalizedIdentifierValue, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentifierValue))
            return Array.Empty<Guid>();

        return identifierType switch
        {
            InvoiceNumberTypeId => await _invoiceRepository
                .FindByNormalizedInvoiceNumberAsync(normalizedIdentifierValue, ct)
                .ContinueWith(t => t.Result.Select(i => i.DocumentId).Distinct().ToList()),
            // We ALSO answer ContractNumber lookups — return invoices referencing the contract.
            // Same caller's normalizedIdentifierValue → query our indexed normalized column.
            // (Pseudo-code; your repo decides the actual query shape.)
            Contracts.ContractIdentifierProvider.ContractNumberTypeId => await _invoiceRepository
                .FindByNormalizedReferencedContractNumberAsync(normalizedIdentifierValue, ct)
                .ContinueWith(t => t.Result.Select(i => i.DocumentId).Distinct().ToList()),
            _ => Array.Empty<Guid>(),
        };
    }

    private static void AddIfPresent(
        List<DocumentIdentifierEntry> entries, string type, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return;
        // Pick a normalization strategy that matches the type's semantic class.
        // The helper class provides two canonical strategies; modules with special
        // semantics may write their own.
        var normalized = DocumentIdentifierNormalization.NormalizeIdentifierCode(rawValue);
        if (string.IsNullOrEmpty(normalized)) return;
        entries.Add(new DocumentIdentifierEntry(type, rawValue.Trim(), normalized));
    }
}
```

That's it — ABP DI picks up the `ITransientDependency` and `RelationDiscoveryService`
discovers your provider through `IEnumerable<IDocumentIdentifierProvider>`. Next time
a document classified into your module's territory enters the L2 pipeline, your
provider participates.

### 2. Store the normalized form

The lookup side queries an **indexed normalized column**, not raw text. Don't make
your module re-compute normalization on every query — your aggregate root should
maintain the normalized field alongside the raw field. See `Contract.cs` and
`Contract.NormalizedContractNumber` for the canonical example:

```csharp
public class Invoice : AuditedAggregateRoot<Guid>
{
    public virtual string? InvoiceNumber { get; private set; }
    public virtual string? NormalizedInvoiceNumber { get; private set; }

    public void UpdateInvoiceNumber(string? raw)
    {
        InvoiceNumber = raw;
        NormalizedInvoiceNumber = string.IsNullOrWhiteSpace(raw)
            ? null
            : DocumentIdentifierNormalization.NormalizeIdentifierCode(raw);
    }
}
```

And the EF mapping:

```csharp
b.HasIndex(x => x.NormalizedInvoiceNumber)
    .HasFilter("NormalizedInvoiceNumber IS NOT NULL");
```

### 3. Add a migration

Standard EF migration; your module's existing migration project handles it.

---

## The string contract you must obey

The core layer routes lookups across providers by **exact string equality on the type
identifier**. This makes the contract entirely up to you and the modules you
interoperate with. Three rules to follow:

### Rule 1 — Pick a deliberate spelling and commit to it

If your module's invoice number type is `"Invoices.InvoiceNumber"`, every emit AND
every lookup uses exactly that spelling. Define it once as `public const string` on
your provider (e.g. `InvoiceNumberTypeId`) and reference the constant everywhere.
Within your module this is just basic engineering hygiene; the constant defends against
typos within your own code.

### Rule 2 — Emit and look up with the SAME normalization

`DocumentIdentifierNormalization` provides two canonical helpers:

| Helper | Use for | Result example |
|---|---|---|
| `NormalizeIdentifierCode(raw)` | Identifier numbers — contract numbers, invoice numbers, PO numbers, project codes. Strips every separator, uppercases, NFKC-folds full-width characters. | `"HT-2024-001"` → `"HT2024001"`; `"ＨＴ－２０２４－００１"` → `"HT2024001"` |
| `NormalizeEntityName(raw)` | Names of legal entities, people, organizations. Collapses whitespace, NFKC-folds, trims. Keeps casing and structural punctuation. | `"  上海某某  科技  有限公司  "` → `"上海某某 科技 有限公司"` |

Your `GetIdentifiersAsync` MUST call the same normalization that your storage column
applies. If your invoice repository queries `WHERE NormalizedInvoiceNumber = @value`
and `NormalizedInvoiceNumber` is populated via `NormalizeIdentifierCode`, then your
`GetIdentifiersAsync` must also use `NormalizeIdentifierCode` when computing the
`NormalizedValue` on each emitted entry.

If you need normalization rules these two helpers can't express (e.g. phone numbers,
URLs, ISBN check-digit folding), write your own static helper inside your module's
Domain assembly. The core layer doesn't care which function you use, only that you
use the same one on both sides.

### Rule 3 — Cross-module interoperability flows through public constants, not literals

When your module wants to **match the same type another module emits** — your invoice
module emitting "I reference contract HT-2024-001" so it gets linked to the contract
module's document holding HT-2024-001 — you NEED the type identifier string to match
exactly between the two modules.

Do **not** hard-code the literal `"ContractNumber"` in your invoice provider. Instead:

1. Add a NuGet `PackageReference` to the contracts module's Domain assembly
   (`Dignite.Paperbase.Contracts.Domain`).
2. Reference the public constant directly:
   ```csharp
   Contracts.ContractIdentifierProvider.ContractNumberTypeId
   ```

This makes the string contract a **real compile-time dependency** — the C# compiler
verifies the alias is intact, and your CI fails the moment the contracts module renames
its constant. A typo (`"contractNumber"` with lowercase c) cannot slip through.

For the normalization rule (Rule 2), the same dependency gives you free alignment: you
both call `DocumentIdentifierNormalization.NormalizeIdentifierCode`, which lives in
the shared `Dignite.Paperbase.Abstractions`.

### Why no central type registry?

A central enum like the old `DocumentIdentifierTypes` would seem to enforce string
consistency. In practice it made the core layer the owner of every business concept
(`ContractNumber` belongs to the contracts module, not to the core), and adding a new
shared type became a versioning headache. The current contract makes module ownership
explicit: each module owns the types it defines, exports them as public constants,
and other modules NuGet-reference what they need.

---

## Entity signature provider — step by step

Multi-field matching. Use when:

- No single field is enough to identify the entity (contract numbers differ between
  main and supplement, so they can't match on contract number alone).
- A combined match (parties + year, vendor + PO + amount) is strong evidence of "same
  business entity".

Emit signatures only when **all** fields are populated. Partial signatures collide
noisily with every other partial.

```csharp
public class InvoiceEntitySignatureProvider
    : IDocumentEntitySignatureProvider, ITransientDependency
{
    public const string VendorAndDateSignatureKind = "Invoices.VendorAndDate";

    public const string FieldVendor = "Vendor";
    public const string FieldYear = "Year";

    public IReadOnlyCollection<string> SupportedSignatureKinds { get; } = new[]
    {
        VendorAndDateSignatureKind,
    };

    public async Task<IReadOnlyList<DocumentEntitySignature>> GetSignaturesAsync(
        Guid documentId, CancellationToken ct = default)
    {
        var invoice = await _invoiceRepository.FindByDocumentIdAsync(documentId);
        if (invoice == null) return Array.Empty<DocumentEntitySignature>();

        if (string.IsNullOrEmpty(invoice.NormalizedVendorName)) return Array.Empty<DocumentEntitySignature>();
        if (invoice.InvoiceDate == null) return Array.Empty<DocumentEntitySignature>();

        return new[]
        {
            new DocumentEntitySignature(
                Kind: VendorAndDateSignatureKind,
                Fields: new Dictionary<string, string>
                {
                    [FieldVendor] = invoice.NormalizedVendorName!,
                    [FieldYear] = invoice.InvoiceDate.Value.Year.ToString("D4"),
                }),
        };
    }

    public async Task<IReadOnlyList<Guid>> FindDocumentsBySignatureAsync(
        DocumentEntitySignature signature, CancellationToken ct = default)
    {
        if (signature.Kind != VendorAndDateSignatureKind) return Array.Empty<Guid>();
        if (!signature.Fields.TryGetValue(FieldVendor, out var vendor)) return Array.Empty<Guid>();
        if (!signature.Fields.TryGetValue(FieldYear, out var yearText)) return Array.Empty<Guid>();
        if (!int.TryParse(yearText, out var year)) return Array.Empty<Guid>();

        var invoices = await _invoiceRepository.FindByVendorAndYearAsync(vendor, year, ct);
        return invoices.Select(i => i.DocumentId).Distinct().ToList();
    }
}
```

The same three rules apply:
- Pick a `Kind` string and stick to it (`"<Module>.<SignatureName>"` convention).
- Normalize field values the same way on emit and lookup.
- Cross-module signature interop (rare, but possible) uses NuGet PackageReference +
  public constants.

---

## What the core layer guarantees

- Calls your provider only with documents that are in the same tenant. Tenant context
  is restored via `CurrentTenant.Change(args.TenantId)` before fan-out — your
  repository's ambient `IMultiTenant` filter does the right thing.
- Routes each identifier type / signature kind to providers that declare it in
  `Supported*Types` / `Supported*Kinds`. Providers not listing the type are not called.
- Per-provider exception isolation. If your `GetIdentifiersAsync` throws, the rest of
  the fan-out continues — your provider's contribution is logged as zero, telemetry
  records the failure, other providers proceed.
- Identifier de-duplication. Multiple providers emitting the same `(Type,
  NormalizedValue)` for the same source document are deduped before lookup.
- Already-linked / dismissed peer skipping. If a `DocumentRelation` already exists
  (including the soft-deleted "user dismissed this suggestion" tombstones), L2 won't
  re-create it.
- Per-`(source, target)` row uniqueness at the database level. Concurrent runs that
  attempt duplicate writes hit a DB unique-constraint violation — your provider's
  duplicates don't pollute the graph.

---

## What we don't guarantee — failure modes you must verify yourself

### Silent type-string mismatch
Provider A emits type `"ContractNumber"`. Provider B emits type `"contractNumber"`.
Same business concept, different strings. Lookups don't route between them. **No
error, no warning** — the relation just doesn't get created.

Detect: cross-module integration test that asserts a known A-B pair generates a
relation. Without that test, the bug surfaces only when a user complains.

### Silent normalization mismatch
Provider A emits `NormalizedValue = "HT2024001"` (NormalizeIdentifierCode applied).
Provider B's repository stores raw `"HT-2024-001"` in the indexed column and the
provider's lookup query does `WHERE Column = @value`. The lookup never matches.

Detect: integration test that writes to provider B's storage via the production code
path (so the normalized column is populated) and then queries through your provider.

### Repeated emit for the same business entity
You emit two identifier entries for the same `(Type, NormalizedValue)` from one
document. The core de-dupes them, but if you also have a `Confidence` differing per
emit, only one wins (first emit). Avoid by checking entry presence in your provider
before emitting.

### High-ambiguity identifier
A single normalized value matches more than `RelationDiscoveryTelemetryRecorder.HighAmbiguityPeerThreshold`
(default 10) distinct peers in one run. The core logs a warning and increments
`paperbase.relation_discovery.l2.high_ambiguity_identifiers`. Almost always this means
your provider emitted a value that's too generic — a stale LLM extraction defaulting
to a common word, a project code that's really shared across thousands of docs, etc.
Find the source by reading the warning's `type` and `normalizedValue` fields.

---

## Verification checklist

Before shipping your module:

- [ ] Integration test: insert a typed record, call your `GetIdentifiersAsync(doc)` and
      assert the expected entries are produced with the expected `NormalizedValue`.
- [ ] Integration test: insert two records sharing the identifier value but with
      surface variation ("HT-2024-001" vs "ht 2024 001"), call L2, assert exactly one
      `DocumentRelation` is created.
- [ ] If you're declaring a cross-module type (Rule 3 above), integration test that
      exercises the full L2 flow with both modules installed.
- [ ] If your provider produces signatures, the same checks for `GetSignaturesAsync`
      and `FindDocumentsBySignatureAsync`.
- [ ] Run with `paperbase.relation_discovery.l2.identifiers_by_provider` telemetry
      visible — confirm your provider shows up and produces non-zero counts on
      documents your module owns.
- [ ] Sanity check `paperbase.relation_discovery.l2.orphan_documents` doesn't spike
      for documents your module classifies — that would mean either your provider
      can't find the typed record (extraction race condition) or it's emitting
      zero identifiers.

---

## Reference implementation

The contracts module is the worked example for both contracts:

- `modules/contracts/src/Dignite.Paperbase.Contracts.Domain/ContractIdentifierProvider.cs`
  — single-field, type constant `ContractNumberTypeId`.
- `modules/contracts/src/Dignite.Paperbase.Contracts.Domain/ContractEntitySignatureProvider.cs`
  — multi-field, signature kind `PartiesAndYearSignatureKind`.
- `modules/contracts/src/Dignite.Paperbase.Contracts.Domain/Contract.cs`
  — aggregate root maintaining `NormalizedContractNumber` / `NormalizedPartyAName` /
  `NormalizedPartyBName` alongside their raw counterparts.
- `modules/contracts/src/Dignite.Paperbase.Contracts.EntityFrameworkCore/EntityFrameworkCore/PaperbaseContractsDbContextModelCreatingExtensions.cs`
  — EF mapping with normalized-column indexes.

Skim those files alongside this doc before starting.

---

## When you outgrow this contract

The contracts above are pragmatic for v1 — single-process modules, single-string
type identifiers. If you find yourself wanting:

- **Per-tenant module assignment** (admin in the back office turns your module on for
  some tenants and off for others) — see Issue #159 Phase α.
- **Cross-service fan-out** (your module is a separate microservice with its own
  database) — see Issue #159 Phase β. The string-only async contract was deliberately
  designed to remote-wrap cleanly.
- **Relation kind inference** (your provider knows whether a match means "Amendment"
  vs "Settlement" vs "Execution") — see Issue #160.
- **Registration-time governance** (auto-detect typo collisions between modules at
  startup) — see Issue #161.

Open an issue, link to your use case, and we can negotiate contract extensions.
