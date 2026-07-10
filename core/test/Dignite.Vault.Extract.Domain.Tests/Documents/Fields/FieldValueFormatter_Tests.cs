using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Domain.Documents.Fields;

/// <summary>
/// #501 item 8. Rendering a typed field-value row used to be three independent <see cref="FieldDataType"/>
/// switches — <c>DocumentExtractedField.SetValue</c> (JSON in), <c>DocumentExtractedField.ToJsonElement</c>
/// (JSON out), and the export's cell renderer — each with its own copy of the date format literals. Adding an
/// enum member meant editing all three, and forgetting one was silent: the value would either round-trip to
/// <c>Undefined</c> (a 500 on the next document read) or land in the file as an empty cell.
/// <para>
/// The two read directions now live in <see cref="FieldValueFormatter"/>. These tests walk
/// <c>Enum.GetValues&lt;FieldDataType&gt;()</c> rather than naming the members, so a new member that misses a
/// branch reddens here rather than shipping. Add a member and run these before doing anything else.
/// </para>
/// </summary>
public class FieldValueFormatter_Tests
{
    /// <summary>One canonical JSON scalar per data type, per the <c>DocumentFieldValue</c> contract.</summary>
    private static readonly IReadOnlyDictionary<FieldDataType, string> CanonicalJson =
        new Dictionary<FieldDataType, string>
        {
            [FieldDataType.Text] = "\"hello\"",
            [FieldDataType.LongText] = "\"a longer body\"",
            [FieldDataType.Number] = "1000.5",
            [FieldDataType.Boolean] = "true",
            [FieldDataType.Date] = "\"2026-03-04\"",
            [FieldDataType.DateTime] = "\"2026-03-04T05:06:07\"",
        };

    [Fact]
    public void The_canonical_sample_set_covers_every_FieldDataType_member()
    {
        // Guards the guard: a new enum member must be given a sample here, or the tests below would silently
        // stop covering it instead of failing.
        var missing = Enum.GetValues<FieldDataType>().Where(t => !CanonicalJson.ContainsKey(t)).ToList();

        missing.ShouldBeEmpty(
            $"add a canonical JSON sample for: {string.Join(", ", missing)} — then check every FieldDataType switch");
    }

    [Fact]
    public void Every_FieldDataType_member_renders_a_cell_string()
    {
        foreach (var dataType in Enum.GetValues<FieldDataType>())
        {
            var row = RowFor(dataType);

            // Not "does it throw ArgumentOutOfRangeException" only: a branch that returned null for a populated
            // row would export a silently empty cell, which is the failure this loud-fail exists to prevent.
            FieldValueFormatter.ToCellString(row, dataType)
                .ShouldNotBeNull($"{dataType} has no branch in FieldValueFormatter.ToCellString");
        }
    }

    [Fact]
    public void Every_FieldDataType_member_renders_a_defined_json_element()
    {
        foreach (var dataType in Enum.GetValues<FieldDataType>())
        {
            var element = FieldValueFormatter.ToJsonElement(RowFor(dataType), dataType);

            // Undefined is the toxic value: it serializes as "Cannot write a JsonElement with ValueKind
            // Undefined" and turns the whole document read into a 500.
            element.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"{dataType} emits an Undefined JsonElement");
            element.ValueKind.ShouldNotBe(JsonValueKind.Null, $"{dataType} lost its value on the way out");
        }
    }

    [Fact]
    public void Every_FieldDataType_member_round_trips_from_canonical_json_and_back()
    {
        // The full loop, covering the third switch (DocumentExtractedField.SetValue) too: canonical JSON in via
        // the aggregate root, canonical JSON out via the formatter. A date literal that drifted between the
        // parse side and the render side reddens here.
        foreach (var dataType in Enum.GetValues<FieldDataType>())
        {
            var fieldDefinitionId = Guid.NewGuid();
            var document = NewDocument();

            document.SetFields(new[]
            {
                new DocumentFieldValue(fieldDefinitionId, dataType, Json(CanonicalJson[dataType])),
            });

            var row = document.ExtractedFieldValues.ShouldHaveSingleItem();

            row.ToJsonElement(dataType).GetRawText().ShouldBe(
                Json(CanonicalJson[dataType]).GetRawText(),
                $"{dataType} does not round-trip through SetValue -> ToJsonElement");
        }
    }

    [Fact]
    public void An_unknown_data_type_loud_fails_on_both_read_directions()
    {
        var row = RowFor(FieldDataType.Text);

        Should.Throw<ArgumentOutOfRangeException>(() => FieldValueFormatter.ToCellString(row, (FieldDataType)99));
        Should.Throw<ArgumentOutOfRangeException>(() => FieldValueFormatter.ToJsonElement(row, (FieldDataType)99));
    }

    [Fact]
    public void The_canonical_date_shapes_are_the_frozen_wire_contract()
    {
        // These are not presentation. They are what DocumentFieldValue requires on the way in, what the REST /
        // MCP ExtractedFields dictionary emits on the way out, and what the #411 fingerprint hashes. Changing
        // either is a wire break plus a silent re-hash of every stored fingerprint.
        FieldValueFormats.Date.ShouldBe("yyyy-MM-dd");
        FieldValueFormats.DateTime.ShouldBe("yyyy-MM-ddTHH:mm:ss");
    }

    private static Document NewDocument() => new(
        Guid.NewGuid(),
        tenantId: null,
        fileOrigin: new FileOrigin(
            blobName: "blobs/x.pdf",
            uploadedByUserName: "test-user",
            contentType: "application/pdf",
            contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            fileSize: 1024,
            originalFileName: "x.pdf"));

    /// <summary>A row populated in exactly the column <paramref name="dataType"/> reads from.</summary>
    private static IFieldValueColumns RowFor(FieldDataType dataType)
    {
        var document = NewDocument();
        document.SetFields(new[] { new DocumentFieldValue(Guid.NewGuid(), dataType, Json(CanonicalJson[dataType])) });
        return document.ExtractedFieldValues.Single();
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
