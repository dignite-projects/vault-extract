using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Unit tests for <see cref="FieldFingerprintCalculator"/> (#411). Pure / deterministic, so no ABP test base —
/// builds <see cref="DocumentExtractedField"/> rows through a <see cref="Document"/> (their constructor is internal)
/// and asserts the canonicalization contract: stable hashing, normalization, partial-key / no-key → null.
/// </summary>
public class FieldFingerprintCalculator_Tests
{
    private static readonly Guid TypeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReceiptNoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AmountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PlainNoteId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static FieldDefinition Field(Guid id, string name, FieldDataType dataType, bool isUniqueKey) =>
        new(id, tenantId: null, documentTypeId: TypeId, name: name, displayName: name, prompt: null,
            dataType: dataType, displayOrder: 0, isRequired: false, allowMultiple: false, isUniqueKey: isUniqueKey);

    private static IReadOnlyCollection<DocumentExtractedField> Values(params DocumentFieldValue[] values)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            new FileOrigin("b", "u", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1, "f.pdf"));
        doc.SetFields(values);
        return doc.ExtractedFieldValues;
    }

    private static DocumentFieldValue Text(Guid id, string value) =>
        new(id, FieldDataType.Text, JsonSerializer.SerializeToElement(value));

    private static DocumentFieldValue Number(Guid id, decimal value) =>
        new(id, FieldDataType.Number, JsonSerializer.SerializeToElement(value));

    /// <summary>One canonical JSON scalar per data type, mirroring <c>FieldValueFormatter_Tests</c>.</summary>
    private static readonly IReadOnlyDictionary<FieldDataType, string> CanonicalJson =
        new Dictionary<FieldDataType, string>
        {
            [FieldDataType.Text] = "\"INV-001\"",
            [FieldDataType.LongText] = "\"a longer body\"",
            [FieldDataType.Number] = "1000.5",
            [FieldDataType.Boolean] = "true",
            [FieldDataType.Date] = "\"2026-03-04\"",
            [FieldDataType.DateTime] = "\"2026-03-04T05:06:07\"",
        };

    [Fact]
    public void Every_FieldDataType_member_canonicalizes_into_the_fingerprint()
    {
        // #501 item 8 follow-up. Canonicalize is the fourth FieldDataType switch, and the only one that still
        // defaults SILENTLY (`_ => null`) rather than loud-failing. A null canonical makes Compute return null,
        // so a new enum member used as a unique key would quietly switch duplicate detection OFF for every
        // document of that type — a missed duplicate, reported as "no fingerprint", indistinguishable from a
        // legitimately partial key.
        //
        // Left silent on purpose: this runs inside the #411 background-job fingerprint path, where throwing
        // would rethrow into the retry loop. So the guard is this test rather than a throw. It walks the enum
        // instead of naming members, so a new one reddens here.
        foreach (var dataType in Enum.GetValues<FieldDataType>())
        {
            var definitionId = Guid.NewGuid();
            var defs = new[] { Field(definitionId, "key", dataType, isUniqueKey: true) };
            var values = Values(new DocumentFieldValue(definitionId, dataType, Json(CanonicalJson[dataType])));

            FieldFingerprintCalculator.Compute(values, defs)
                .ShouldNotBeNull($"{dataType} has no branch in FieldFingerprintCalculator.Canonicalize");
        }
    }

    [Fact]
    public void The_canonical_sample_set_covers_every_FieldDataType_member()
    {
        // Guards the guard: without a sample, the walk above would skip the new member instead of failing.
        var missing = Enum.GetValues<FieldDataType>().Where(t => !CanonicalJson.ContainsKey(t)).ToList();

        missing.ShouldBeEmpty($"add a canonical JSON sample for: {string.Join(", ", missing)}");
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void No_Unique_Key_Fields_Returns_Null()
    {
        var defs = new[] { Field(PlainNoteId, "note", FieldDataType.Text, isUniqueKey: false) };
        var values = Values(Text(PlainNoteId, "anything"));

        FieldFingerprintCalculator.Compute(values, defs).ShouldBeNull();
    }

    [Fact]
    public void Partial_Key_Missing_A_Unique_Field_Returns_Null()
    {
        var defs = new[]
        {
            Field(ReceiptNoId, "receipt_no", FieldDataType.Text, isUniqueKey: true),
            Field(AmountId, "amount", FieldDataType.Number, isUniqueKey: true)
        };
        // Only receipt_no extracted; amount missing.
        var values = Values(Text(ReceiptNoId, "R-001"));

        FieldFingerprintCalculator.Compute(values, defs).ShouldBeNull();
    }

    [Fact]
    public void Same_Values_Produce_Stable_64_Char_Hex_Fingerprint()
    {
        var defs = new[] { Field(ReceiptNoId, "receipt_no", FieldDataType.Text, isUniqueKey: true) };

        var a = FieldFingerprintCalculator.Compute(Values(Text(ReceiptNoId, "R-001")), defs);
        var b = FieldFingerprintCalculator.Compute(Values(Text(ReceiptNoId, "R-001")), defs);

        a.ShouldNotBeNull();
        a!.Length.ShouldBe(64);
        a.ShouldAllBe(c => Uri.IsHexDigit(c));
        b.ShouldBe(a);
    }

    [Fact]
    public void Text_Normalization_Folds_Case_And_Whitespace()
    {
        var defs = new[] { Field(ReceiptNoId, "receipt_no", FieldDataType.Text, isUniqueKey: true) };

        var a = FieldFingerprintCalculator.Compute(Values(Text(ReceiptNoId, "  Inv  001 ")), defs);
        var b = FieldFingerprintCalculator.Compute(Values(Text(ReceiptNoId, "inv 001")), defs);

        b.ShouldBe(a);
    }

    [Fact]
    public void Number_Normalization_Strips_Trailing_Zeros()
    {
        var defs = new[] { Field(AmountId, "amount", FieldDataType.Number, isUniqueKey: true) };

        var a = FieldFingerprintCalculator.Compute(Values(Number(AmountId, 100m)), defs);
        var b = FieldFingerprintCalculator.Compute(Values(Number(AmountId, 100.00m)), defs);

        b.ShouldBe(a);
    }

    [Fact]
    public void Different_Values_Produce_Different_Fingerprints()
    {
        var defs = new[] { Field(ReceiptNoId, "receipt_no", FieldDataType.Text, isUniqueKey: true) };

        var a = FieldFingerprintCalculator.Compute(Values(Text(ReceiptNoId, "R-001")), defs);
        var b = FieldFingerprintCalculator.Compute(Values(Text(ReceiptNoId, "R-002")), defs);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Only_Unique_Key_Fields_Participate()
    {
        var defs = new[]
        {
            Field(ReceiptNoId, "receipt_no", FieldDataType.Text, isUniqueKey: true),
            Field(PlainNoteId, "note", FieldDataType.Text, isUniqueKey: false)
        };

        // Same unique key, different non-key note -> same fingerprint.
        var a = FieldFingerprintCalculator.Compute(
            Values(Text(ReceiptNoId, "R-001"), Text(PlainNoteId, "first scan")), defs);
        var b = FieldFingerprintCalculator.Compute(
            Values(Text(ReceiptNoId, "R-001"), Text(PlainNoteId, "second scan")), defs);

        b.ShouldBe(a);
    }
}
