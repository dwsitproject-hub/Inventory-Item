using System.Globalization;
using ClosedXML.Excel;

namespace BcInventory.Api;

/// <summary>
/// Parser for .xlsx workbooks — both the vendor report templates (WIP, Aset &amp; Sparepart,
/// Bahan Baku, Barang Jadi, BC 3.0) and the blank templates this system hands out for
/// re-upload. Columns are mapped by header text, so a user may reorder or omit columns.
/// Templates are identified by header fingerprint, never by file name — except Bahan Baku vs
/// Barang Jadi, whose headers are byte-identical, where the sheet/file name is the only
/// available discriminator (FR-I8/I9).
/// </summary>
public static class XlsxParser
{
    public static bool IsXlsx(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static readonly string[] IdMonths =
    { "JANUARI", "FEBRUARI", "MARET", "APRIL", "MEI", "JUNI", "JULI", "AGUSTUS", "SEPTEMBER", "OKTOBER", "NOVEMBER", "DESEMBER" };

    private static readonly System.Text.RegularExpressions.Regex DateRx =
        new(@"\b(\d{2})[.\-/](\d{2})[.\-/](\d{4})\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Reporting period from a sheet title / header line. Handles an Indonesian month name
    /// ("Laporan WIP JULI 2026") and an explicit range ("PERIODE 01.07.2026 - 31.07.2026",
    /// also "S/D" / "SD"), where the start date's month is the period. Returns the 1st of it.
    /// </summary>
    public static DateOnly? PeriodFrom(string text)
    {
        var upper = (text ?? "").ToUpperInvariant();
        for (int i = 0; i < IdMonths.Length; i++)
        {
            var idx = upper.IndexOf(IdMonths[i], StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = upper[(idx + IdMonths[i].Length)..].Trim();
            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;
            var year = int.Parse(digits);
            if (year < 100) year += 2000;
            if (year is < 2000 or > 2100) continue;
            return new DateOnly(year, i + 1, 1);
        }

        var m = DateRx.Match(upper);
        if (m.Success
            && int.TryParse(m.Groups[2].Value, out var mm)
            && int.TryParse(m.Groups[3].Value, out var yy)
            && mm is >= 1 and <= 12 && yy is >= 2000 and <= 2100)
            return new DateOnly(yy, mm, 1);

        return null;
    }

    private static string Cell(IXLWorksheet ws, int row, int col)
    {
        var c = ws.Cell(row, col);
        if (c.IsEmpty()) return "";
        if (c.DataType == XLDataType.DateTime && c.Value.IsDateTime)
            return c.Value.GetDateTime().ToString("yyyy-MM-dd");
        // Take the stored number, never the formatted string: these sheets display quantities
        // rounded to 0 decimals (8201.657 renders as "8,202"), and saldo must not be rounded.
        if (c.DataType == XLDataType.Number && c.Value.IsNumber)
            return c.Value.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture);
        return (c.GetFormattedString() ?? "").Trim();
    }

    private static string Norm(string s) => s.Trim().ToUpperInvariant();

    /// <summary>Header labels for one column: composite "Group / Sub" for two-row headers.</summary>
    private static string[] HeaderNames(IXLWorksheet ws, int headerRow, int headerRows, int lastCol)
    {
        var names = new string[lastCol + 1];
        string carriedTop = "";
        var topUsedWithSub = new HashSet<string>();

        for (int c = 1; c <= lastCol; c++)
        {
            var top = Cell(ws, headerRow, c);
            if (top.Length > 0) carriedTop = top;
            if (headerRows == 1) { names[c] = top; continue; }

            var sub = Cell(ws, headerRow + 1, c);
            if (sub.Length > 0)
            {
                names[c] = carriedTop.Length > 0 && !carriedTop.Equals(sub, StringComparison.OrdinalIgnoreCase)
                    ? carriedTop + " / " + sub
                    : sub;
                if (carriedTop.Length > 0) topUsedWithSub.Add(carriedTop);
            }
            else
            {
                // A blank sub-header under a group that already has named children is an
                // unlabelled column in the source (BC 3.0 has one under "Dokumen").
                names[c] = topUsedWithSub.Contains(carriedTop) && top.Length == 0
                    ? carriedTop + " / (unlabelled)"
                    : carriedTop;
            }
        }
        return names;
    }

    private record Layout(Catalog.Report Report, int HeaderRow, int HeaderRows, Dictionary<int, Field> Columns, List<string> Unmapped);

    private static Layout? Identify(IXLWorksheet ws, string fileName)
    {
        var lastCol = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 120);
        var scanTo = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 12);
        if (lastCol == 0) return null;

        for (int r = 1; r <= scanTo; r++)
        {
            var rowVals = Enumerable.Range(1, lastCol).Select(c => Cell(ws, r, c)).ToArray();
            if (rowVals.Count(v => v.Length > 0) < 3) continue;

            // A two-row header shows group names on top (e.g. "Dok. Pabean" over "Nomor"/"Tanggal").
            var twoRow = r < scanTo && Enumerable.Range(1, lastCol)
                .Any(c => Cell(ws, r, c).Length > 0 && Cell(ws, r + 1, c).Length > 0
                          && Catalog.Reports.Any(rep => rep.Fields.Any(f =>
                              Norm(f.Name) == Norm(Cell(ws, r, c) + " / " + Cell(ws, r + 1, c)))));

            var headerRows = twoRow ? 2 : 1;
            var names = HeaderNames(ws, r, headerRows, lastCol);

            Layout? best = null;
            int bestMatched = 0;
            var tied = new List<string>();

            foreach (var report in Catalog.Reports)
            {
                var byHeader = new Dictionary<string, Field>();
                foreach (var f in report.Fields)
                {
                    byHeader[Norm(f.Name)] = f;
                    byHeader.TryAdd(Norm(f.Display), f);      // accept the display label too
                }
                var cols = new Dictionary<int, Field>();
                var unmapped = new List<string>();
                for (int c = 1; c <= lastCol; c++)
                {
                    var n = names[c];
                    if (n.Length == 0) continue;
                    if (byHeader.TryGetValue(Norm(n), out var f)) cols[c] = f;
                    else if (!Norm(n).Equals("NO")) unmapped.Add(n);
                }
                if (cols.Count < 3 || cols.Count < report.Fields.Length * 0.5) continue;

                if (cols.Count > bestMatched)
                {
                    bestMatched = cols.Count;
                    best = new Layout(report, r, headerRows, cols, unmapped);
                    tied = new List<string> { report.Template };
                }
                else if (cols.Count == bestMatched && best is not null)
                {
                    tied.Add(report.Template);
                }
            }
            if (best is null) continue;

            // Several reports share a column layout byte-for-byte (Bahan Baku / Barang Jadi,
            // Aset dan Sparepart / Scraps). Only the sheet or file name can separate them.
            if (tied.Count > 1)
            {
                // the title line above the header often names the report even when the sheet is "Sheet1"
                var title = r > 1 ? Cell(ws, r - 1, 1) : "";
                var hint = Norm($"{ws.Name} {fileName} {title}");
                var pick = tied.FirstOrDefault(t =>
                {
                    var rep = Catalog.Reports.First(x => x.Template == t);
                    return (rep.NameHints ?? Array.Empty<string>()).Any(h => hint.Contains(Norm(h)));
                });
                if (pick is null)
                {
                    var titles = tied.Select(t => Catalog.Reports.First(x => x.Template == t).Title);
                    throw new InvalidDataException(
                        $"This layout matches {string.Join(" and ", titles)} equally (identical column headers); " +
                        "the sheet name, file name or title row must say which report it is.");
                }
                best = best with { Report = Catalog.Reports.First(x => x.Template == pick) };
            }
            return best;
        }
        return null;
    }

    public static string? SniffTemplate(byte[] bytes, string fileName)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var wb = new XLWorkbook(ms);
            foreach (var ws in wb.Worksheets)
                if (Identify(ws, fileName) is { } l) return l.Report.Template;
        }
        catch (InvalidDataException) { throw; }
        catch { /* not a readable workbook */ }
        return null;
    }

    public static ParseResult Parse(byte[] bytes, string fileName)
    {
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);

        IXLWorksheet? sheet = null;
        Layout? layout = null;
        foreach (var ws in wb.Worksheets)
        {
            layout = Identify(ws, fileName);
            if (layout != null) { sheet = ws; break; }
        }
        if (sheet is null || layout is null)
            throw new InvalidDataException("No recognised report layout found in this workbook.");

        var meta = new Dictionary<string, string>
        {
            ["sheet"] = sheet.Name,
            ["template"] = layout.Report.Template
        };
        if (layout.Unmapped.Count > 0)                      // FR-I10: never silently drop a column
            meta["unmappedColumns"] = string.Join(" · ", layout.Unmapped.Take(20));
        if (layout.HeaderRow > 1)
        {
            var title = Cell(sheet, layout.HeaderRow - 1, 1);
            if (title.Length > 0) meta["title"] = title;
        }
        // In-document metadata beats the file name, which anyone may have renamed — the Scraps
        // export ships with a title reading July 2026 inside a file named "…2019 SD 2026".
        var period = PeriodFrom(sheet.Name)
                     ?? PeriodFrom(meta.GetValueOrDefault("title", ""))
                     ?? PeriodFrom(fileName);
        if (period is { } p) meta["period"] = p.ToString("yyyy-MM-dd");

        var firstDataRow = layout.HeaderRow + layout.HeaderRows;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lines = new List<ParsedLine>();

        for (int r = firstDataRow; r <= lastRow; r++)
        {
            var data = new Dictionary<string, object?>();
            var problems = new List<string>();

            foreach (var (col, field) in layout.Columns)
            {
                var raw = Cell(sheet, r, col);
                if (raw.Length == 0) continue;
                data[field.Name] = Ingestion.NormalizeValue(raw, field.Type, problems, field.Name);
            }
            if (data.Count == 0) continue;

            var first = Cell(sheet, r, 1);
            if (first.Equals("Total", StringComparison.OrdinalIgnoreCase)) continue;

            lines.Add(new ParsedLine(r, data, problems));
        }

        return new ParseResult(lines, meta, new Dictionary<string, string>());
    }
}
