using System;
using System.Text;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Issue #100: structured assembly of the per-turn system prompt for
/// <c>DocumentChatAppService</c>. Replaces the ad-hoc string concatenation that grew
/// inside <c>PrepareAgentSetupAsync</c> as boundary rules / anchor hints / multi-step
/// reasoning guidance accumulated. Each segment is rendered on its own line so
/// downstream prompt-cache hashing stays stable when only one segment changes.
/// </summary>
internal static class ChatInstructionsBuilder
{
    /// <summary>
    /// Multi-step / cross-document reasoning guidance appended to the system prompt
    /// of every chat turn. Steers the model toward "structured tool first → vector
    /// search second" (better grounding for reconciliation / aggregation queries) and
    /// away from treating the conversation's anchor document as a hard scope.
    /// </summary>
    public const string MultiStepReasoningGuidance =
        "You can chain tools across one turn:\n" +
        "  0) [Optional — when an anchor document id is present AND the question implies linked documents " +
             "(payments, receipts, attachments, amendments, predecessors)] " +
             "call get_document_relations(anchorDocumentId) first to discover related document ids. " +
             "Pass those ids into search_paperbase_documents(documentIds=[...]) for precise retrieval.\n" +
        "  1) Pull structured fields with a business tool (e.g. get_contract_detail, search_contracts, get_contract_aggregate).\n" +
        "  2) Follow up with search_paperbase_documents if cross-document evidence is needed — " +
             "pass documentIds returned by step 0 or step 1, and/or a documentTypeCode (e.g. 'receipt.general') to focus the vector search.\n" +
        "  3) Compare structured values against retrieved chunks before answering.\n" +
        "get_document_relations vs search_paperbase_documents: use get_document_relations when you know " +
             "a specific document id and want its explicit graph neighbors (manual or AI-suggested links); " +
             "use search_paperbase_documents when the question is semantic with no known starting document.\n" +
        "Reconciliation example: 'has this contract been paid?' → get_document_relations(anchorId) → " +
             "search_paperbase_documents(documentIds=returned_ids, documentTypeCode='receipt.general') → match → answer.\n" +
        "If a question references multiple document types or implies cross-document evidence, do not stay inside the anchor document. The anchor is a hint, never a hard scope.";

    public static string Build(
        string baseInstructions,
        string boundaryRule,
        string? anchorContext,
        string multiStepGuidance)
    {
        if (baseInstructions is null) throw new ArgumentNullException(nameof(baseInstructions));
        if (boundaryRule is null) throw new ArgumentNullException(nameof(boundaryRule));
        if (multiStepGuidance is null) throw new ArgumentNullException(nameof(multiStepGuidance));

        var sb = new StringBuilder(
            capacity: baseInstructions.Length + boundaryRule.Length + (anchorContext?.Length ?? 0) + multiStepGuidance.Length + 16);

        sb.Append(baseInstructions);
        AppendSection(sb, boundaryRule);
        if (!string.IsNullOrEmpty(anchorContext))
        {
            AppendSection(sb, anchorContext);
        }
        AppendSection(sb, multiStepGuidance);

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string segment)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
        sb.Append('\n');
        sb.Append(segment);
    }
}
