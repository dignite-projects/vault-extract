using System.Linq;
using Dignite.Paperbase.Contracts.Contracts;
using Dignite.Paperbase.Contracts.EventHandlers;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts.EventHandlers;

/// <summary>
/// Unit tests for the seven LLM-tier validation rules in <see cref="ContractExtractionValidator"/>.
/// Pure-function tests; no DI required.
/// </summary>
public class ContractExtractionValidator_Tests
{
    private readonly ContractExtractionValidator _validator = new();

    // Convenience: a minimal valid baseline so each test can override one field.
    private static ContractExtractionResult Valid() => new()
    {
        Title = "業務委託契約書",
        ContractNumber = "CNT-2026-001",
        PartyAName = "甲社",
        PartyBName = "乙社",
        TotalAmount = 1_000_000m,
        Currency = "JPY",
        SignedDate = "2026-04-01",
        EffectiveDate = "2026-04-01",
        ExpirationDate = "2027-03-31",
        ExtractionConfidence = 0.9
    };

    [Fact]
    public void Valid_Baseline_Passes_All_Rules()
    {
        var r = _validator.Validate(Valid());

        r.IsValid.ShouldBeTrue();
        r.Errors.ShouldBeEmpty();
        r.Warnings.ShouldBeEmpty();
    }

    // ── Rule 1: TotalAmount non-negative ────────────────────────────────────────

    [Fact]
    public void Negative_TotalAmount_Triggers_Error()
    {
        var input = Valid();
        input.TotalAmount = -1m;

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Message.Contains("TotalAmount") && e.Message.Contains("non-negative"));
    }

    [Fact]
    public void Null_TotalAmount_Is_Accepted()
    {
        var input = Valid();
        input.TotalAmount = null;

        _validator.Validate(input).IsValid.ShouldBeTrue();
    }

    // ── Rule 2: Currency is ISO 4217 ────────────────────────────────────────────

    [Fact]
    public void Non_Iso4217_Currency_Triggers_Error()
    {
        var input = Valid();
        input.Currency = "JPY円";   // not three ASCII uppercase letters

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Message.Contains("Currency") && e.Message.Contains("ISO 4217"));
    }

    [Fact]
    public void Lowercase_Currency_Triggers_Error()
    {
        var input = Valid();
        input.Currency = "jpy";

        _validator.Validate(input).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Empty_Currency_Is_Accepted()
    {
        // Empty currency is filled in downstream (ContractFields defaults to JPY).
        var input = Valid();
        input.Currency = null;

        _validator.Validate(input).IsValid.ShouldBeTrue();
    }

    // ── Rule 3: ISO 8601 dates ──────────────────────────────────────────────────

    [Fact]
    public void Malformed_Date_Triggers_Error()
    {
        var input = Valid();
        input.SignedDate = "2026/04/01";   // slashes, not ISO

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Message.Contains("SignedDate") && e.Message.Contains("ISO 8601"));
    }

    // ── Rule 4: EffectiveDate ≤ ExpirationDate ──────────────────────────────────

    [Fact]
    public void Effective_After_Expiration_Triggers_Error()
    {
        var input = Valid();
        input.EffectiveDate = "2027-04-01";
        input.ExpirationDate = "2026-04-01";

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Message.Contains("EffectiveDate") && e.Message.Contains("on or before ExpirationDate"));
    }

    [Fact]
    public void Effective_Equals_Expiration_Is_Accepted()
    {
        // A one-day contract is unusual but not invalid.
        var input = Valid();
        input.EffectiveDate = "2026-04-01";
        input.ExpirationDate = "2026-04-01";

        _validator.Validate(input).IsValid.ShouldBeTrue();
    }

    // ── Rule 5: At least one of Title / PartyAName / PartyBName / CounterpartyName ─

    [Fact]
    public void All_Name_Fields_Empty_Triggers_Error()
    {
        var input = Valid();
        input.Title = null;
        input.PartyAName = null;
        input.PartyBName = null;
        input.CounterpartyName = null;

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Message.Contains("At least one of Title"));
    }

    [Fact]
    public void Counterparty_Only_Is_Sufficient()
    {
        var input = Valid();
        input.Title = null;
        input.PartyAName = null;
        input.PartyBName = null;
        input.CounterpartyName = "相手方株式会社";

        _validator.Validate(input).IsValid.ShouldBeTrue();
    }

    // ── Rule 6: TerminationNoticeDays in [0, 365] (warning) ────────────────────

    [Fact]
    public void Termination_Notice_Out_Of_Range_Is_Warning_Not_Error()
    {
        var input = Valid();
        input.TerminationNoticeDays = 400;

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeTrue();
        r.Warnings.ShouldContain(w => w.Message.Contains("TerminationNoticeDays") && w.Message.Contains("400"));
    }

    // ── Rule 7: Low confidence (warning) ───────────────────────────────────────

    [Fact]
    public void Low_Confidence_Is_Warning_Not_Error()
    {
        var input = Valid();
        input.ExtractionConfidence = 0.3;

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeTrue();
        r.Warnings.ShouldContain(w => w.Message.Contains("ExtractionConfidence") && w.Message.Contains("0.30"));
    }

    // ── Aggregate behavior ──────────────────────────────────────────────────────

    [Fact]
    public void Errors_Carry_Stable_Rule_Codes_For_Telemetry()
    {
        // Telemetry dashboards key off RuleCode (low-cardinality, language-neutral),
        // so every error must populate it from ContractExtractionValidator.RuleCodes.
        var input = Valid();
        input.TotalAmount = -1m;
        input.Currency = "yen";

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.Any(e => e.RuleCode == ContractExtractionValidator.RuleCodes.TotalAmountNonNegative).ShouldBeTrue();
        r.Errors.Any(e => e.RuleCode == ContractExtractionValidator.RuleCodes.CurrencyIso4217).ShouldBeTrue();
    }

    [Fact]
    public void Multiple_Errors_Are_All_Reported_For_Single_Retry()
    {
        // Strategy: surface every violation in one go so the LLM can fix them all
        // in a single retry rather than one-error-per-roundtrip.
        var input = Valid();
        input.TotalAmount = -5m;
        input.Currency = "yen";
        input.EffectiveDate = "2030-01-01";
        input.ExpirationDate = "2020-01-01";

        var r = _validator.Validate(input);

        r.IsValid.ShouldBeFalse();
        r.Errors.Count.ShouldBeGreaterThanOrEqualTo(3);
        r.Errors.Any(e => e.Message.Contains("TotalAmount")).ShouldBeTrue();
        r.Errors.Any(e => e.Message.Contains("Currency")).ShouldBeTrue();
        r.Errors.Any(e => e.Message.Contains("EffectiveDate")).ShouldBeTrue();
    }
}
