using Dignite.Paperbase.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 硬伤一 Phase 1 regression guard: identifier value normalization. Two documents that share
/// the same business identifier (contract number, etc.) MUST map to the same normalized form
/// regardless of casing, punctuation, whitespace, or full-width-vs-half-width variations —
/// otherwise L2 RelationDiscovery silently fails to match them.
/// </summary>
public class DocumentIdentifierNormalization_Tests
{
    // ── NormalizeIdentifierCode: contract/invoice/PO/project numbers ──────────────

    [Theory]
    [InlineData("HT-2024-001", "HT2024001")]
    [InlineData("HT2024001",   "HT2024001")]                                            // already normalized
    [InlineData("ht-2024-001", "HT2024001")]                                            // case-insensitive
    [InlineData("  HT-2024-001  ", "HT2024001")]                                        // surrounding whitespace
    [InlineData("HT 2024 001", "HT2024001")]                                            // spaces as separators
    [InlineData("HT/2024/001", "HT2024001")]                                            // slashes
    [InlineData("HT.2024.001", "HT2024001")]                                            // dots
    [InlineData("HT_2024_001", "HT2024001")]                                            // underscores
    [InlineData("HT—2024—001", "HT2024001")]                                            // em-dash (U+2014)
    [InlineData("HT–2024–001", "HT2024001")]                                            // en-dash (U+2013)
    [InlineData("HT－2024－001", "HT2024001")]                                          // full-width hyphen (U+FF0D)
    [InlineData("ＨＴ２０２４００１", "HT2024001")]                                       // full-width letters+digits
    [InlineData("ＨＴ－２０２４－００１", "HT2024001")]                                    // full-width + full-width dash
    [InlineData("HT--2024--001", "HT2024001")]                                          // double dashes
    public void NormalizeIdentifierCode_Should_Collapse_Equivalent_Forms(string raw, string expected)
    {
        DocumentIdentifierNormalization.NormalizeIdentifierCode(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("HT-2024-001", "HT-2024-002")]                                          // different sequence
    [InlineData("HT-2024-001", "PO-2024-001")]                                          // different prefix
    [InlineData("HT-2024-001", "HT-2025-001")]                                          // different year
    public void NormalizeIdentifierCode_Different_Identifiers_Must_Stay_Distinct(string a, string b)
    {
        var na = DocumentIdentifierNormalization.NormalizeIdentifierCode(a);
        var nb = DocumentIdentifierNormalization.NormalizeIdentifierCode(b);
        na.ShouldNotBe(nb);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("---")]                                                                 // separators only
    [InlineData("///")]
    public void NormalizeIdentifierCode_Empty_Or_Punctuation_Only_Should_Return_Empty(string? raw)
    {
        DocumentIdentifierNormalization.NormalizeIdentifierCode(raw!).ShouldBe(string.Empty);
    }

    [Fact]
    public void NormalizeIdentifierCode_Should_Be_Idempotent()
    {
        // Property: normalize(normalize(x)) == normalize(x). L2 + provider may re-normalize
        // defensively; idempotence guarantees that re-running normalization on the canonical
        // form doesn't drift.
        var once = DocumentIdentifierNormalization.NormalizeIdentifierCode("HT-2024-001");
        var twice = DocumentIdentifierNormalization.NormalizeIdentifierCode(once);
        twice.ShouldBe(once);
    }

    // ── NormalizeEntityName: party / company names ────────────────────────────────

    [Theory]
    [InlineData("上海某某科技有限公司", "上海某某科技有限公司")]
    [InlineData("  上海某某科技有限公司  ", "上海某某科技有限公司")]                    // trim
    [InlineData("上海某某 科技 有限公司", "上海某某 科技 有限公司")]                     // preserve single inner spaces
    [InlineData("上海某某  科技  有限公司", "上海某某 科技 有限公司")]                   // collapse runs
    [InlineData("上海某某　科技　有限公司", "上海某某 科技 有限公司")]                   // full-width space → ASCII space
    [InlineData("XYZ Co., Ltd.", "XYZ Co., Ltd.")]                                      // English punctuation preserved
    [InlineData("XYZ (国际) Ltd.", "XYZ (国际) Ltd.")]                                   // bracket structure preserved
    public void NormalizeEntityName_Should_Collapse_Whitespace_Preserve_Structure(string raw, string expected)
    {
        DocumentIdentifierNormalization.NormalizeEntityName(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("　　　")]                                                              // full-width spaces only
    public void NormalizeEntityName_Empty_Should_Return_Empty(string? raw)
    {
        DocumentIdentifierNormalization.NormalizeEntityName(raw!).ShouldBe(string.Empty);
    }

    // ── Dispatch: Normalize(type, value) routes to the right strategy ─────────────

    [Theory]
    [InlineData(DocumentIdentifierTypes.ContractNumber, "HT-2024-001", "HT2024001")]
    [InlineData(DocumentIdentifierTypes.PoNumber,        "PO-2024-001", "PO2024001")]
    [InlineData(DocumentIdentifierTypes.InvoiceNumber,   "INV.2024.001", "INV2024001")]
    [InlineData(DocumentIdentifierTypes.ProjectCode,     "PROJ_2024_001", "PROJ2024001")]
    [InlineData(DocumentIdentifierTypes.PartyName,       "  上海某某  有限公司  ", "上海某某 有限公司")]
    [InlineData("Contracts.SerialCode",                  "ABC-001", "ABC001")]          // module-private type → code strategy
    public void Normalize_Dispatches_By_Type(string type, string raw, string expected)
    {
        DocumentIdentifierNormalization.Normalize(type, raw).ShouldBe(expected);
    }
}
