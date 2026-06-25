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
