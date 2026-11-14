using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.JSInterop;

namespace TNM.Management.Services;

/// <summary>
/// Generates standard OpenXML (.xlsx) Excel workbooks with styled headers, borders, auto-fit column widths,
/// and numeric formatting so exported lists across the entire system open cleanly in Excel.
/// </summary>
public static class ExcelExporter
{
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static async Task DownloadAsync(
        IJSRuntime js,
        string fileNamePrefix,
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<object?[]> rows)
    {
        var bytes = BuildWorkbook(sheetName, headers, rows);
        var cleanPrefix = SanitizeFileName(fileNamePrefix);
        var fileName = $"{cleanPrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        var base64 = Convert.ToBase64String(bytes);
        await js.InvokeVoidAsync("tnmDownloadExcel", fileName, base64);
    }

    public static byte[] BuildWorkbook(
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<object?[]> rows)
    {
        var rowList = rows as IReadOnlyList<object?[]> ?? rows.ToList();
        var safeSheetName = SanitizeSheetName(sheetName);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteXmlEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
            WriteXmlEntry(archive, "_rels/.rels", BuildRootRelsXml());
            WriteXmlEntry(archive, "xl/workbook.xml", BuildWorkbookXml(safeSheetName));
            WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
            WriteXmlEntry(archive, "xl/styles.xml", BuildStylesXml());
            WriteXmlEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(headers, rowList));
        }
        return ms.ToArray();
    }

    private static void WriteXmlEntry(ZipArchive archive, string entryPath, XDocument doc)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false
        });
        doc.Save(writer);
    }

    private static XDocument BuildContentTypesXml() =>
        new(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ContentTypesNs + "Types",
                new XElement(ContentTypesNs + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypesNs + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(ContentTypesNs + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(ContentTypesNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(ContentTypesNs + "Override",
                    new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))
            ));

    private static XDocument BuildRootRelsXml() =>
        new(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(PkgRelNs + "Relationships",
                new XElement(PkgRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))
            ));

    private static XDocument BuildWorkbookXml(string sheetName) =>
        new(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(MainNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs.NamespaceName),
                new XElement(MainNs + "sheets",
                    new XElement(MainNs + "sheet",
                        new XAttribute("name", sheetName),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1"))
                )));

    private static XDocument BuildWorkbookRelsXml() =>
        new(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(PkgRelNs + "Relationships",
                new XElement(PkgRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(PkgRelNs + "Relationship",
                    new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml"))
            ));

    private static XDocument BuildStylesXml() =>
        new(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(MainNs + "styleSheet",
                new XElement(MainNs + "numFmts",
                    new XAttribute("count", "1"),
                    new XElement(MainNs + "numFmt",
                        new XAttribute("numFmtId", "164"),
                        new XAttribute("formatCode", "#,##0"))
                ),
                new XElement(MainNs + "fonts",
                    new XAttribute("count", "2"),
                    new XElement(MainNs + "font",
                        new XElement(MainNs + "sz", new XAttribute("val", "11")),
                        new XElement(MainNs + "color", new XAttribute("rgb", "FF0F172A")),
                        new XElement(MainNs + "name", new XAttribute("val", "Segoe UI"))
                    ),
                    new XElement(MainNs + "font",
                        new XElement(MainNs + "b"),
                        new XElement(MainNs + "sz", new XAttribute("val", "11")),
                        new XElement(MainNs + "color", new XAttribute("rgb", "FFFFFFFF")),
                        new XElement(MainNs + "name", new XAttribute("val", "Segoe UI"))
                    )
                ),
                new XElement(MainNs + "fills",
                    new XAttribute("count", "3"),
                    new XElement(MainNs + "fill",
                        new XElement(MainNs + "patternFill", new XAttribute("patternType", "none"))
                    ),
                    new XElement(MainNs + "fill",
                        new XElement(MainNs + "patternFill", new XAttribute("patternType", "gray125"))
                    ),
                    new XElement(MainNs + "fill",
                        new XElement(MainNs + "patternFill",
                            new XAttribute("patternType", "solid"),
                            new XElement(MainNs + "fgColor", new XAttribute("rgb", "FF0F766E")),
                            new XElement(MainNs + "bgColor", new XAttribute("indexed", "64"))
                        )
                    )
                ),
                new XElement(MainNs + "borders",
                    new XAttribute("count", "2"),
                    new XElement(MainNs + "border",
                        new XElement(MainNs + "left"),
                        new XElement(MainNs + "right"),
                        new XElement(MainNs + "top"),
                        new XElement(MainNs + "bottom"),
                        new XElement(MainNs + "diagonal")
                    ),
                    new XElement(MainNs + "border",
                        new XElement(MainNs + "left", new XAttribute("style", "thin"), new XElement(MainNs + "color", new XAttribute("rgb", "FFCBD5E1"))),
                        new XElement(MainNs + "right", new XAttribute("style", "thin"), new XElement(MainNs + "color", new XAttribute("rgb", "FFCBD5E1"))),
                        new XElement(MainNs + "top", new XAttribute("style", "thin"), new XElement(MainNs + "color", new XAttribute("rgb", "FFCBD5E1"))),
                        new XElement(MainNs + "bottom", new XAttribute("style", "thin"), new XElement(MainNs + "color", new XAttribute("rgb", "FFCBD5E1"))),
                        new XElement(MainNs + "diagonal")
                    )
                ),
                new XElement(MainNs + "cellStyleXfs",
                    new XAttribute("count", "1"),
                    new XElement(MainNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "0"))
                ),
                new XElement(MainNs + "cellXfs",
                    new XAttribute("count", "5"),
                    // 0: default
                    new XElement(MainNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "0"),
                        new XAttribute("xfId", "0")),
                    // 1: header cell (bold white text, teal background, border)
                    new XElement(MainNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "1"),
                        new XAttribute("fillId", "2"),
                        new XAttribute("borderId", "1"),
                        new XAttribute("xfId", "0"),
                        new XAttribute("applyFont", "1"),
                        new XAttribute("applyFill", "1"),
                        new XAttribute("applyBorder", "1"),
                        new XAttribute("applyAlignment", "1"),
                        new XElement(MainNs + "alignment",
                            new XAttribute("horizontal", "center"),
                            new XAttribute("vertical", "center"),
                            new XAttribute("wrapText", "1"))),
                    // 2: text cell (left aligned, border)
                    new XElement(MainNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "1"),
                        new XAttribute("xfId", "0"),
                        new XAttribute("applyFont", "1"),
                        new XAttribute("applyBorder", "1"),
                        new XAttribute("applyAlignment", "1"),
                        new XElement(MainNs + "alignment",
                            new XAttribute("vertical", "center"),
                            new XAttribute("wrapText", "1"))),
                    // 3: numeric / currency cell (#,##0, right aligned, border)
                    new XElement(MainNs + "xf",
                        new XAttribute("numFmtId", "164"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "1"),
                        new XAttribute("xfId", "0"),
                        new XAttribute("applyNumberFormat", "1"),
                        new XAttribute("applyFont", "1"),
                        new XAttribute("applyBorder", "1"),
                        new XAttribute("applyAlignment", "1"),
                        new XElement(MainNs + "alignment",
                            new XAttribute("horizontal", "right"),
                            new XAttribute("vertical", "center"))),
                    // 4: center cell (STT, border)
                    new XElement(MainNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "1"),
                        new XAttribute("xfId", "0"),
                        new XAttribute("applyFont", "1"),
                        new XAttribute("applyBorder", "1"),
                        new XAttribute("applyAlignment", "1"),
                        new XElement(MainNs + "alignment",
                            new XAttribute("horizontal", "center"),
                            new XAttribute("vertical", "center")))
                )
            ));

    private static XDocument BuildWorksheetXml(IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
    {
        var colCount = Math.Max(1, headers.Count);
        var maxLengths = new int[colCount];
        for (var c = 0; c < headers.Count; c++)
        {
            maxLengths[c] = Math.Max(6, (headers[c] ?? "").Length + 4);
        }

        foreach (var row in rows)
        {
            var limit = Math.Min(colCount, row.Length);
            for (var c = 0; c < limit; c++)
            {
                var s = FormatCellPreview(row[c]);
                var firstLineLen = s.Split('\n').Max(l => l.Length);
                if (firstLineLen > maxLengths[c])
                    maxLengths[c] = firstLineLen;
            }
        }

        var colsElement = new XElement(MainNs + "cols");
        for (var c = 0; c < colCount; c++)
        {
            var width = Math.Clamp(maxLengths[c] + 4, 8, 52);
            if (c == 0 && headers[0].Equals("STT", StringComparison.OrdinalIgnoreCase))
                width = 8;
            colsElement.Add(new XElement(MainNs + "col",
                new XAttribute("min", (c + 1).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("max", (c + 1).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("width", width.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("customWidth", "1")));
        }

        var sheetData = new XElement(MainNs + "sheetData");

        // Row 1: Header
        var headerRow = new XElement(MainNs + "row",
            new XAttribute("r", "1"),
            new XAttribute("ht", "26"),
            new XAttribute("customHeight", "1"));
        for (var c = 0; c < headers.Count; c++)
        {
            var cellRef = GetColumnName(c) + "1";
            headerRow.Add(CreateInlineStringCell(cellRef, headers[c] ?? "", styleIndex: 1));
        }
        sheetData.Add(headerRow);

        // Data rows
        for (var rIdx = 0; rIdx < rows.Count; rIdx++)
        {
            var excelRowNum = rIdx + 2;
            var rowValues = rows[rIdx];
            var rowEl = new XElement(MainNs + "row",
                new XAttribute("r", excelRowNum.ToString(CultureInfo.InvariantCulture)));

            var limit = Math.Min(colCount, rowValues.Length);
            for (var c = 0; c < limit; c++)
            {
                var cellRef = GetColumnName(c) + excelRowNum.ToString(CultureInfo.InvariantCulture);
                var val = rowValues[c];
                if (c == 0 && IsNumeric(val, out var sttNum))
                {
                    rowEl.Add(CreateNumericCell(cellRef, sttNum, styleIndex: 4));
                }
                else if (IsNumeric(val, out var numStr))
                {
                    rowEl.Add(CreateNumericCell(cellRef, numStr, styleIndex: 3));
                }
                else
                {
                    var text = SanitizeXmlString(val?.ToString() ?? "");
                    rowEl.Add(CreateInlineStringCell(cellRef, text, styleIndex: 2));
                }
            }
            sheetData.Add(rowEl);
        }

        var lastColName = GetColumnName(colCount - 1);
        var lastRowNum = Math.Max(1, rows.Count + 1);

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(MainNs + "worksheet",
                new XAttribute(XNamespace.Xmlns + "r", RelNs.NamespaceName),
                new XElement(MainNs + "sheetViews",
                    new XElement(MainNs + "sheetView",
                        new XAttribute("tabSelected", "1"),
                        new XAttribute("workbookViewId", "0"),
                        new XElement(MainNs + "pane",
                            new XAttribute("ySplit", "1"),
                            new XAttribute("topLeftCell", "A2"),
                            new XAttribute("activePane", "bottomLeft"),
                            new XAttribute("state", "frozen"))
                    )
                ),
                new XElement(MainNs + "sheetFormatPr",
                    new XAttribute("defaultRowHeight", "20")),
                colsElement,
                sheetData,
                new XElement(MainNs + "autoFilter",
                    new XAttribute("ref", $"A1:{lastColName}{lastRowNum}"))
            ));
    }

    private static XElement CreateInlineStringCell(string cellRef, string text, int styleIndex)
    {
        var tEl = new XElement(MainNs + "t", SanitizeXmlString(text));
        if (text.StartsWith(' ') || text.EndsWith(' ') || text.Contains('\n') || text.Contains('\r'))
        {
            tEl.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }
        return new XElement(MainNs + "c",
            new XAttribute("r", cellRef),
            new XAttribute("s", styleIndex.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("t", "inlineStr"),
            new XElement(MainNs + "is", tEl));
    }

    private static XElement CreateNumericCell(string cellRef, string invariantNum, int styleIndex) =>
        new(MainNs + "c",
            new XAttribute("r", cellRef),
            new XAttribute("s", styleIndex.ToString(CultureInfo.InvariantCulture)),
            new XElement(MainNs + "v", invariantNum));

    private static bool IsNumeric(object? value, out string invariantNumber)
    {
        switch (value)
        {
            case byte b:
                invariantNumber = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case short s:
                invariantNumber = s.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i:
                invariantNumber = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case long l:
                invariantNumber = l.ToString(CultureInfo.InvariantCulture);
                return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                invariantNumber = f.ToString(CultureInfo.InvariantCulture);
                return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                invariantNumber = d.ToString(CultureInfo.InvariantCulture);
                return true;
            case decimal m:
                invariantNumber = m.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                invariantNumber = "";
                return false;
        }
    }

    private static string FormatCellPreview(object? val)
    {
        if (val is null) return "";
        if (val is long l) return l.ToString("N0", CultureInfo.InvariantCulture);
        if (val is int i) return i.ToString("N0", CultureInfo.InvariantCulture);
        if (val is decimal m) return m.ToString("N0", CultureInfo.InvariantCulture);
        return val.ToString() ?? "";
    }

    private static string GetColumnName(int zeroBasedIndex)
    {
        var dividend = zeroBasedIndex + 1;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }
        return columnName;
    }

    private static string SanitizeXmlString(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (XmlConvert.IsXmlChar(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string SanitizeSheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Data";
        var invalid = new[] { '\\', '/', '?', '*', ':', '[', ']' };
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Data";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "tnm-export";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }
        var res = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(res) ? "tnm-export" : res;
    }
}
