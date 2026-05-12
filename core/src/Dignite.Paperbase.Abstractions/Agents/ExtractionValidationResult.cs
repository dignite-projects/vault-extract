using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Paperbase.Abstractions.Agents;

/// <summary>
/// A single validation rule violation: the rule's stable code (for telemetry / dashboards
/// — low-cardinality, language-neutral) paired with the human / LLM-readable message.
/// Rule codes use the form <c>Module.Field.Rule</c> (e.g. <c>Contracts.TotalAmount.NonNegative</c>).
/// </summary>
public sealed record ExtractionRuleViolation(string RuleCode, string Message);

/// <summary>
/// Outcome of validating a structured extraction result. <see cref="Errors"/> is what the
/// retry middleware feeds back to the LLM verbatim, so messages must be self-contained
/// (include the offending value and the rule that was violated). <see cref="Warnings"/>
/// is informational only — does not trigger a retry; useful for telemetry tags.
/// <para>
/// Each violation carries a stable <see cref="ExtractionRuleViolation.RuleCode"/> for
/// low-cardinality telemetry tagging in addition to the natural-language
/// <see cref="ExtractionRuleViolation.Message"/> fed back to the LLM.
/// </para>
/// </summary>
public sealed record ExtractionValidationResult(
    bool IsValid,
    IReadOnlyList<ExtractionRuleViolation> Errors,
    IReadOnlyList<ExtractionRuleViolation> Warnings)
{
    public static ExtractionValidationResult Ok() =>
        new(true, Array.Empty<ExtractionRuleViolation>(), Array.Empty<ExtractionRuleViolation>());

    public static ExtractionValidationResult Failed(params ExtractionRuleViolation[] errors) =>
        new(false, errors, Array.Empty<ExtractionRuleViolation>());

    /// <summary>
    /// Convenience used by tests / synthetic validators: build a failure carrying just
    /// the messages with an opaque <c>generic</c> rule code. Production validators should
    /// emit explicit <see cref="ExtractionRuleViolation"/>s so telemetry can tag by rule.
    /// </summary>
    public static ExtractionValidationResult Failed(params string[] errorMessages) =>
        new(false,
            errorMessages.Select(m => new ExtractionRuleViolation("generic", m)).ToArray(),
            Array.Empty<ExtractionRuleViolation>());
}
