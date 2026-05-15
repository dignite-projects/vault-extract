using System.Collections.Generic;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// A multi-field business "entity signature" — what business modules emit to mark
/// documents as referring to the same underlying business object even when no single
/// identifier is shared.
///
/// <para>
/// <strong>Why single-field identifiers aren't enough</strong>: a contract and its
/// supplement agreement may not share <c>ContractNumber</c> (the supplement has its own),
/// but they share <c>(PartyA, PartyB, signing year)</c> — together a strong signal that
/// they're related. Single-field <see cref="IDocumentIdentifierProvider"/> can't express
/// this because emitting only <c>PartyA</c> would create a noise graph (one supplier
/// has hundreds of contracts). The signature joins all three fields in one comparison
/// and gives the relationship a real chance of being found while keeping noise low.
/// </para>
///
/// <para>
/// <strong>Contract</strong>:
/// <list type="bullet">
/// <item><see cref="Kind"/> is module-defined and module-private (e.g. <c>"Contracts.PartiesAndYear"</c>).
/// L2 fan-out matches signatures only against providers that declare the same <c>Kind</c> in
/// their <see cref="IDocumentEntitySignatureProvider.SupportedSignatureKinds"/>.</item>
/// <item><see cref="Fields"/> values MUST be already normalized by the provider. The L2
/// service compares signatures by exact-string equality of all field values; "上海某某  有限公司"
/// vs "上海某某 有限公司" will not match unless the provider canonicalized whitespace first.</item>
/// <item>If ANY field value is empty/whitespace, the provider should NOT emit the signature
/// at all (incomplete signatures cause cross-document false positives — every
/// "PartyA=ACME, PartyB=null" matches every other "PartyA=ACME, PartyB=null").</item>
/// </list>
/// </para>
/// </summary>
/// <param name="Kind">Module-namespaced signature category, e.g. <c>"Contracts.PartiesAndYear"</c>.</param>
/// <param name="Fields">Normalized field values composing the signature. Comparison is by
/// the full dictionary's content (key + value), so two signatures match iff every (key, value)
/// pair is identical.</param>
public sealed record DocumentEntitySignature(
    string Kind,
    IReadOnlyDictionary<string, string> Fields);
