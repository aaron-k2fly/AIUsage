using System.IO.Compression;
using System.Text;
using AIUsage.Export;

namespace AIUsage.Tests;

public class XlsxWriterTests
{
    /// <summary>Read one entry's text out of the produced .xlsx (a zip).</summary>
    private static string ReadEntry(byte[] xlsx, string path)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(path) ?? throw new Xunit.Sdk.XunitException($"missing zip entry: {path}");
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static byte[] Build(IReadOnlyList<string> headers, params IReadOnlyList<object?>[] rows) =>
        XlsxWriter.Build("Sheet", headers, rows);

    [Fact]
    public void Build_produces_a_valid_zip_with_the_expected_parts()
    {
        var xlsx = Build(["A"], [1L]);

        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();

        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/_rels/workbook.xml.rels", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);
    }

    [Fact]
    public void Build_writes_header_row_then_data_rows()
    {
        var sheet = ReadEntry(Build(["Name", "Count"], ["widget", 42L]), "xl/worksheets/sheet1.xml");

        Assert.Contains("<row r=\"1\">", sheet); // header
        Assert.Contains("<row r=\"2\">", sheet); // first data row
        Assert.Contains("widget", sheet);
        Assert.Contains("<v>42</v>", sheet);
    }

    [Fact]
    public void Build_types_numbers_as_values_and_text_as_inline_strings()
    {
        var sheet = ReadEntry(Build(["n", "s"], [7L, "hello"]), "xl/worksheets/sheet1.xml");

        Assert.Contains("<v>7</v>", sheet);                 // numeric cell: bare <v>
        Assert.Contains("t=\"inlineStr\"", sheet);          // text cell: inline string
        Assert.Contains("<t xml:space=\"preserve\">hello</t>", sheet);
    }

    [Fact]
    public void Build_emits_an_empty_cell_for_null()
    {
        var sheet = ReadEntry(Build(["a", "b"], [null, "x"]), "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"A2\"/>", sheet); // self-closing empty cell
    }

    [Fact]
    public void Build_escapes_xml_special_characters()
    {
        var sheet = ReadEntry(Build(["h"], ["a<b>&\"c\""]), "xl/worksheets/sheet1.xml");

        Assert.Contains("&lt;b&gt;", sheet);
        Assert.Contains("&amp;", sheet);
        Assert.Contains("&quot;", sheet);
        Assert.DoesNotContain("<b>", sheet); // raw angle brackets must not leak through
    }

    [Fact]
    public void Build_maps_columns_past_z_to_double_letters()
    {
        // 28 headers -> columns A..Z, AA, AB
        var headers = Enumerable.Range(0, 28).Select(i => $"c{i}").ToList();
        var sheet = ReadEntry(XlsxWriter.Build("S", headers, []), "xl/worksheets/sheet1.xml");

        Assert.Contains("r=\"Z1\"", sheet);
        Assert.Contains("r=\"AA1\"", sheet);
        Assert.Contains("r=\"AB1\"", sheet);
    }

    [Fact]
    public void Build_sanitizes_the_sheet_name()
    {
        var workbook = ReadEntry(XlsxWriter.Build("Bad:Name/[x]", ["h"], []), "xl/workbook.xml");
        Assert.Contains("name=\"BadNamex\"", workbook); // illegal chars [ ] : / stripped
    }

    [Fact]
    public void Build_truncates_a_long_sheet_name_to_31_chars()
    {
        var longName = new string('x', 50);
        var workbook = ReadEntry(XlsxWriter.Build(longName, ["h"], []), "xl/workbook.xml");
        Assert.Contains($"name=\"{new string('x', 31)}\"", workbook);
    }
}
