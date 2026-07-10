using System;
using System.Collections.Generic;
using Dignite.Vault.Extract.Documents.Fields;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Unit tests for <see cref="ExportCellRenderer"/>. Internal and visible through InternalsVisibleTo; a pure
/// function, so no DB and no mocks.
/// <para>
/// These exist because the equivalent assertions could not be made through the EF export tests. SQLite hands
/// back <c>DocumentExtractedField</c> child rows in primary-key order <c>(DocumentId, FieldDefinitionId, Order)</c>,
/// which is already ascending by <c>Order</c> — so the integration test that claims to prove the multi-value
/// join "never relies on the DB's return order" stayed green with the sort deleted. Here the input sequence is
/// the test's to choose, so the sort is genuinely pinned.
/// </para>
/// </summary>
public class ExportCellRenderer_Tests
{
    [Fact]
    public void Renders_a_multi_value_field_ascending_by_Order_whatever_order_the_rows_arrive_in()
    {
        // Deliberately 2, 0, 1 — the sequence a caller could receive from an unordered child subquery.
        var values = new List<ExtractedFieldProjection>
        {
            new() { Order = 2, TextValue = "2026" },
            new() { Order = 0, TextValue = "urgent" },
            new() { Order = 1, TextValue = "legal" },
        };

        ExportCellRenderer.RenderCell(values, FieldDataType.Text).ShouldBe("urgent; legal; 2026");
    }

    [Fact]
    public void Renders_a_single_value_field_as_the_bare_value_with_no_separator()
    {
        var values = new List<ExtractedFieldProjection> { new() { Order = 0, TextValue = "sole" } };

        ExportCellRenderer.RenderCell(values, FieldDataType.Text).ShouldBe("sole");
    }

    [Fact]
    public void Renders_no_values_as_an_empty_cell()
    {
        ExportCellRenderer.RenderCell(Array.Empty<ExtractedFieldProjection>(), FieldDataType.Text).ShouldBeNull();
    }

    [Fact]
    public void Skips_a_row_whose_typed_column_for_this_data_type_is_null()
    {
        // A row carrying no value for the declared type contributes nothing — and must not leave a stray
        // separator behind.
        var values = new List<ExtractedFieldProjection>
        {
            new() { Order = 0, TextValue = "kept" },
            new() { Order = 1, TextValue = null },
        };

        ExportCellRenderer.RenderCell(values, FieldDataType.Text).ShouldBe("kept");
    }

    [Fact]
    public void Renders_each_data_type_in_its_canonical_shape()
    {
        // ExtractedFieldProjection is internal, so rows are built inline rather than passed as theory data —
        // an internal type may not appear in a public test method's signature.
        Render(new() { TextValue = "hello" }, FieldDataType.Text).ShouldBe("hello");
        Render(new() { LongTextValue = "body" }, FieldDataType.LongText).ShouldBe("body");

        // Minimal shape: no six trailing zeros from decimal(38,6).
        Render(new() { NumberValue = 1000m }, FieldDataType.Number).ShouldBe("1000");
        Render(new() { NumberValue = 10.50m }, FieldDataType.Number).ShouldBe("10.5");

        Render(new() { BooleanValue = true }, FieldDataType.Boolean).ShouldBe("true");
        Render(new() { BooleanValue = false }, FieldDataType.Boolean).ShouldBe("false");

        Render(new() { DateValue = new DateOnly(2026, 3, 4) }, FieldDataType.Date).ShouldBe("2026-03-04");
        Render(new() { DateTimeValue = new DateTime(2026, 3, 4, 5, 6, 7) }, FieldDataType.DateTime)
            .ShouldBe("2026-03-04T05:06:07");
    }

    private static string? Render(ExtractedFieldProjection row, FieldDataType dataType)
        => ExportCellRenderer.RenderCell(new[] { row }, dataType);

    [Fact]
    public void Loud_fails_on_a_data_type_it_does_not_know_rather_than_exporting_an_empty_cell()
    {
        // A new FieldDataType member that misses the switch must break loudly. A silently empty cell in a file
        // handed to an accountant is worse than an error.
        var values = new[] { new ExtractedFieldProjection { TextValue = "x" } };

        Should.Throw<ArgumentOutOfRangeException>(() => ExportCellRenderer.RenderCell(values, (FieldDataType)99));
    }
}
