using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Issue #115 L2: business module → L2 contract for single-field identifier matching.
///
/// <para>
/// <strong>Design principle</strong>: business modules are the single source of truth for
/// their own identifier data. <see cref="Contracts.Contract.ContractNumber"/> lives in the
/// contract module's database; the core layer never holds a redundant copy. L2 RelationDiscovery
/// fans out across all installed providers to find peer documents that share an identifier
/// value with the source.
/// </para>
///
/// <para>
/// <strong>Open by design (Issue #159)</strong>: <see cref="SupportedIdentifierTypes"/> is an
/// open string collection — the core does NOT know which type strings exist in the system.
/// Business modules can name their types freely; we recommend a <c>"&lt;ModuleName&gt;.&lt;TypeName&gt;"</c>
/// prefix (e.g. <c>"HR.EmployeeId"</c>) for module-private types to avoid name collisions with
/// other modules. <see cref="DocumentIdentifierTypes"/> provides constants for types that are
/// genuinely cross-module (parties, project codes, contract numbers shared between contracts and
/// invoices); using those constants is opt-in. Adding a new business module requires NO change
/// to this contract, to <see cref="DocumentIdentifierTypes"/>, or to any core code — only your
/// own provider implementation registered as <see cref="Volo.Abp.DependencyInjection.ITransientDependency"/>.
/// </para>
///
/// <para>
/// <strong>Normalization is provider-owned</strong>: comparing identifier values for "same business
/// entity" requires normalization (case-fold, strip separators, NFKC for full-width vs half-width,
/// etc. — see <see cref="DocumentIdentifierNormalization"/>). L2 does NOT apply normalization
/// centrally because it doesn't know your type's semantic class. Instead:
/// <list type="bullet">
/// <item><see cref="GetIdentifiersAsync"/> returns <see cref="DocumentIdentifierEntry"/> records
/// that already carry BOTH the raw display form AND the normalized comparison key — your
/// responsibility to compute the normalized key correctly for your type.</item>
/// <item><see cref="FindDocumentsAsync"/> receives the source's already-normalized value (passed
/// straight through by L2). Your repository lookup must compare against an already-normalized
/// column (e.g. <see cref="Contracts.Contract.NormalizedContractNumber"/>) using the SAME
/// normalization rule you used in <see cref="GetIdentifiersAsync"/>.</item>
/// </list>
/// Two modules that both declare the same type string MUST use compatible normalization rules
/// (otherwise cross-module match silently fails). This is a governance contract between modules,
/// not enforced by code.
/// </para>
///
/// <para>
/// <strong>Multi-tenancy</strong>: implementations use ABP <see cref="Volo.Abp.MultiTenancy.IMultiTenant"/>
/// ambient filter on their repositories; the L2 background job sets
/// <c>CurrentTenant.Change(args.TenantId)</c> before invoking providers, so queries are
/// automatically tenant-scoped.
/// </para>
///
/// <para>
/// <strong>Future-proof for microservices (Issue #159)</strong>: the contract is async,
/// arguments are all serializable strings/Guids, and there's no shared in-process state. When
/// business modules move to standalone microservices, a remote-call wrapper implementing this
/// interface (gRPC/HTTP client) plugs in transparently.
/// </para>
/// </summary>
public interface IDocumentIdentifierProvider
{
    /// <summary>
    /// The identifier type strings this provider handles. L2 fan-out routes a lookup to this
    /// provider iff the source's identifier type is in this collection. Open string set —
    /// see contract docs.
    /// </summary>
    IReadOnlyCollection<string> SupportedIdentifierTypes { get; }

    /// <summary>
    /// Returns the identifiers the given document holds. Each entry carries the raw display
    /// form (for UI / description) and the normalized comparison key (for matching). The
    /// provider is responsible for producing the normalized value consistent with how it
    /// stores / compares its own data — see contract docs.
    ///
    /// <para>
    /// Returns an empty list if this provider does not own the document (e.g. a contract
    /// provider receiving an invoice's document id) — do NOT throw.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse lookup. <paramref name="normalizedIdentifierValue"/> is the already-normalized
    /// comparison key from the source's <see cref="DocumentIdentifierEntry.NormalizedValue"/>;
    /// the provider compares it against its own indexed normalized column.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindDocumentsAsync(
        string identifierType,
        string normalizedIdentifierValue,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An identifier the provider has emitted for a document — carries the raw display form
/// AND the normalized comparison key.
/// </summary>
/// <param name="Type">Identifier type — module-defined string; see contract docs on
/// <see cref="IDocumentIdentifierProvider"/>.</param>
/// <param name="Value">Raw value the user / source data carries (e.g. <c>"HT-2024-001"</c>) —
/// used for UI display and L2 relation descriptions.</param>
/// <param name="NormalizedValue">Comparison key (e.g. <c>"HT2024001"</c>) — produced by the
/// provider so cross-module matching works regardless of casing / separator / full-width
/// variation. Two providers handling the same <paramref name="Type"/> string MUST use
/// compatible normalization to interoperate.</param>
public sealed record DocumentIdentifierEntry(string Type, string Value, string NormalizedValue);
