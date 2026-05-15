using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Contracts;

/// <summary>
/// The contract module's multi-field entity signature contributor.
///
/// <para>
/// Emits one signature kind — <see cref="PartiesAndYearSignatureKind"/> — whose fields are the
/// normalized <see cref="Contract.NormalizedPartyAName"/>, <see cref="Contract.NormalizedPartyBName"/>,
/// and <see cref="Contract.SignedDate"/>'s year. The signature only fires when ALL three
/// fields are populated; partial signatures would collide noisily across hundreds of contracts.
/// </para>
///
/// <para>
/// <strong>Why this exists alongside the identifier provider</strong>:
/// <see cref="ContractIdentifierProvider"/> only exposes <c>ContractNumber</c>. Most "obviously
/// related" contracts in real data do NOT share a contract number — supplements get their own
/// number, main + addendum get separate numbers, framework + order contracts each have their
/// own. They DO share <c>(PartyA, PartyB, year)</c>. The signature path lets RelationDiscovery
/// find them while sidestepping the "one supplier has 100 contracts" noise problem that would
/// happen if PartyA were exposed as a single-field identifier.
/// </para>
/// </summary>
public class ContractEntitySignatureProvider : IDocumentEntitySignatureProvider, ITransientDependency
{
    public const string PartiesAndYearSignatureKind = "Contracts.PartiesAndYear";

    public const string FieldPartyA = "PartyA";
    public const string FieldPartyB = "PartyB";
    public const string FieldYear = "Year";

    public IReadOnlyCollection<string> SupportedSignatureKinds { get; } = new[]
    {
        PartiesAndYearSignatureKind,
    };

    private readonly IContractRepository _contractRepository;

    public ContractEntitySignatureProvider(IContractRepository contractRepository)
    {
        _contractRepository = contractRepository;
    }

    public virtual async Task<IReadOnlyList<DocumentEntitySignature>> GetSignaturesAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var contract = await _contractRepository.FindByDocumentIdAsync(documentId);
        if (contract == null)
        {
            return Array.Empty<DocumentEntitySignature>();
        }

        // Require ALL three fields. Missing field → no signature (avoids "PartyA=X, PartyB=null"
        // false-positive cascade across all contracts with the same single party).
        if (string.IsNullOrEmpty(contract.NormalizedPartyAName)) return Array.Empty<DocumentEntitySignature>();
        if (string.IsNullOrEmpty(contract.NormalizedPartyBName)) return Array.Empty<DocumentEntitySignature>();
        if (contract.SignedDate == null) return Array.Empty<DocumentEntitySignature>();

        return new[]
        {
            new DocumentEntitySignature(
                Kind: PartiesAndYearSignatureKind,
                Fields: new Dictionary<string, string>
                {
                    [FieldPartyA] = contract.NormalizedPartyAName!,
                    [FieldPartyB] = contract.NormalizedPartyBName!,
                    [FieldYear] = contract.SignedDate.Value.Year.ToString("D4"),
                }),
        };
    }

    public virtual async Task<IReadOnlyList<Guid>> FindDocumentsBySignatureAsync(
        DocumentEntitySignature signature,
        CancellationToken cancellationToken = default)
    {
        // Defensive: only respond to our own kind.
        if (!string.Equals(signature.Kind, PartiesAndYearSignatureKind, StringComparison.Ordinal))
        {
            return Array.Empty<Guid>();
        }

        if (!signature.Fields.TryGetValue(FieldPartyA, out var partyA) || string.IsNullOrWhiteSpace(partyA))
            return Array.Empty<Guid>();
        if (!signature.Fields.TryGetValue(FieldPartyB, out var partyB) || string.IsNullOrWhiteSpace(partyB))
            return Array.Empty<Guid>();
        if (!signature.Fields.TryGetValue(FieldYear, out var yearText) || !int.TryParse(yearText, out var year))
            return Array.Empty<Guid>();

        var contracts = await _contractRepository.FindByPartiesAndYearAsync(partyA, partyB, year, cancellationToken);
        return contracts
            .Select(c => c.DocumentId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }
}
