namespace Dignite.Paperbase.Documents;

/// <summary>
/// Vocabulary of identifier type strings that are likely shared across business modules.
///
/// <para>
/// <strong>This is NOT a closed enum.</strong> The core layer accepts any string as an
/// identifier type — see <see cref="IDocumentIdentifierProvider.SupportedIdentifierTypes"/>.
/// The constants below exist purely as convenience and as a coordination point: if two business
/// modules want their identifiers to match each other (a contract module's contract number
/// matching an invoice module's referenced contract number), they should both declare the
/// SAME type string. Using the constants here is the recommended way to do that without
/// typo'ing the string literal.
/// </para>
///
/// <para>
/// <strong>Module-private types</strong> (relationships you don't expect other modules to
/// share) — name them freely with a module prefix, e.g. <c>"HR.EmployeeId"</c>,
/// <c>"Medical.PatientId"</c>. You do NOT need to add anything to this class. Your provider
/// is the single source of truth for what types it handles and how to normalize them.
/// </para>
///
/// <para>
/// <strong>Adding a new shared type</strong>: only add a constant here when (a) it semantically
/// transcends any single business module AND (b) you expect 2+ modules to interoperate on it.
/// Just because a type is "general-sounding" (e.g. <c>"DocumentNumber"</c>) doesn't make it
/// belong here — the question is whether multiple modules will declare it in their
/// <c>SupportedIdentifierTypes</c>.
/// </para>
/// </summary>
public static class DocumentIdentifierTypes
{
    /// <summary>合同编号 (contract number). Used by the contracts module; intentionally
    /// reusable by invoice / order modules that reference the same contract.</summary>
    public const string ContractNumber = "ContractNumber";

    /// <summary>采购订单号 (purchase order number). Cross-module: PO modules emit it,
    /// invoice modules reference it.</summary>
    public const string PoNumber = "PoNumber";

    /// <summary>发票号 (invoice number).</summary>
    public const string InvoiceNumber = "InvoiceNumber";

    /// <summary>
    /// 当事人名称 (party / counterparty name). Highly ambiguous as a SINGLE-field identifier
    /// (one vendor has many contracts); see <see cref="IDocumentEntitySignatureProvider"/> for
    /// multi-field combinations involving party names.
    /// </summary>
    public const string PartyName = "PartyName";

    /// <summary>项目代号 (project code). Cross-module: contract / invoice / report modules
    /// all reference the same project code.</summary>
    public const string ProjectCode = "ProjectCode";
}
