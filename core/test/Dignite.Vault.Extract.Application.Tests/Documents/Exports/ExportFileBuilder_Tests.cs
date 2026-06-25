using System.Collections.Generic;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Serialization tests for <see cref="ExportFileBuilder"/>. It is internal and visible through InternalsVisibleTo.
/// Pure function; no DB or mocks required.
/// </summary>
public class ExportFileBuilder_Tests
{
    [Fact]
    public void Csv_Should_Have_Utf8_Bom_And_Header_And_Row()
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Csv,
            new[] { "Title", "Amount" },
            new List<string?[]> { new[] { "Contract A", "1000" } });

        // UTF-8 BOM: Excel recognizes UTF-8 text correctly.
        bytes[0].ShouldBe((byte)0xEF);
        bytes[1].ShouldBe((byte)0xBB);
        bytes[2].ShouldBe((byte)0xBF);

        var text = Encoding.UTF8.GetString(bytes);
        text.ShouldContain("Title,Amount");
        text.ShouldContain("Contract A,1000");
    }

    [Fact]
    public void Csv_Should_Escape_Comma_Quote_And_Newline()
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Csv,
            new[] { "col" },
            new List<string?[]>
            {
                new[] { "a,b" },
                new[] { "say \"hi\"" },
                new[] { "line1\nline2" }
            });

        var text = Encoding.UTF8.GetString(bytes);
        text.ShouldContain("\"a,b\"");
        text.ShouldContain("\"say \"\"hi\"\"\"");
        text.ShouldContain("\"line1\nline2\"");
    }

    [Fact]
    public void Csv_Should_Render_Null_Cell_As_Empty()
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Csv,
            new[] { "a", "b" },
            new List<string?[]> { new string?[] { null, "x" } });

        Encoding.UTF8.GetString(bytes).ShouldContain(",x");
    }

    [Fact]
    public void Xlsx_Should_Be_Readable_With_Header_And_Values()
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Xlsx,
            new[] { "Title", "Amount" },
            new List<string?[]> { new[] { "Contract A", "1000" } });

        using var ms = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(ms);
        var ws = workbook.Worksheet(1);

        ws.Cell(1, 1).GetString().ShouldBe("Title");
        ws.Cell(1, 2).GetString().ShouldBe("Amount");
        ws.Cell(2, 1).GetString().ShouldBe("Contract A");
        ws.Cell(2, 2).GetString().ShouldBe("1000");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1+2")]
    [InlineData("@SUM(A1)")]
    [InlineData("  =evil")] // Leading spaces + =
    [InlineData("\t=evil")] // Leading tab
    public void Csv_Should_Neutralize_Formula_Injection(string dangerous)
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Csv,
            new[] { "col" },
            new List<string?[]> { new[] { dangerous } });

        // Dangerous values are neutralized with a leading apostrophe, so Excel / Sheets no longer execute them as formulas.
        Encoding.UTF8.GetString(bytes).ShouldContain("'" + dangerous);
    }

    [Fact]
    public void Csv_Should_Neutralize_Dangerous_Header()
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Csv,
            new[] { "=cmd|'/c calc'!A1" },
            new List<string?[]> { new[] { "x" } });

        Encoding.UTF8.GetString(bytes).ShouldContain("'=cmd");
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("123")]
    [InlineData("a=b")] // = is not at the start, so the value should not be changed.
    public void Csv_Should_Not_Touch_Safe_Values(string safe)
    {
        var bytes = ExportFileBuilder.Build(
            ExportFormat.Csv,
            new[] { "col" },
            new List<string?[]> { new[] { safe } });

        Encoding.UTF8.GetString(bytes).ShouldNotContain("'" + safe);
    }
}
