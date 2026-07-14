using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AIUsage.Export;

/// <summary>
/// Minimal, dependency-free .xlsx (OOXML) writer for a single worksheet. Emits inline
/// strings (no shared-strings table) and numeric cells; enough for flat data exports that
/// open cleanly in Excel / LibreOffice / Google Sheets.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Build(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", ContentTypes);
            Write(zip, "_rels/.rels", RootRels);
            Write(zip, "xl/workbook.xml", Workbook(SanitizeSheetName(sheetName)));
            Write(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
            Write(zip, "xl/worksheets/sheet1.xml", Sheet(headers, rows));
        }
        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private const string ContentTypes =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private const string RootRels =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string WorkbookRels =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;

    private static string Workbook(string sheetName) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="{EscapeXml(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    private static string Sheet(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");

        var rowNum = 1;
        sb.Append($"<row r=\"{rowNum}\">");
        for (var c = 0; c < headers.Count; c++)
            sb.Append(Cell(ColLetter(c) + rowNum, headers[c]));
        sb.Append("</row>");

        foreach (var row in rows)
        {
            rowNum++;
            sb.Append($"<row r=\"{rowNum}\">");
            for (var c = 0; c < row.Count; c++)
                sb.Append(Cell(ColLetter(c) + rowNum, row[c]));
            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string Cell(string reference, object? value)
    {
        if (value is null) return $"<c r=\"{reference}\"/>";
        if (value is long or int or short or byte or double or float or decimal)
            return $"<c r=\"{reference}\"><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>";
        return $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeXml(value.ToString() ?? "")}</t></is></c>";
    }

    private static string ColLetter(int index) // 0-based -> A, B, ... Z, AA, ...
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    private static string EscapeXml(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // strip characters that are illegal in XML 1.0 (Excel rejects the file otherwise)
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) continue;
            sb.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => ch.ToString()
            });
        }
        return sb.ToString();
    }

    private static string SanitizeSheetName(string name)
    {
        var cleaned = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Sheet1";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
