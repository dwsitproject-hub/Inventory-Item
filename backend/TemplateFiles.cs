using ClosedXML.Excel;

namespace BcInventory.Api;

/// <summary>
/// Blank upload templates users can download, fill in and upload back. The data sheet carries
/// only the header row — deliberately no example row, because an unedited template must never
/// load junk. Guidance and a worked example live on a second sheet the parser ignores.
/// </summary>
public static class TemplateFiles
{
    private static readonly XLColor Navy = XLColor.FromHtml("#0E2841");
    private static readonly XLColor Chip = XLColor.FromHtml("#EAF0F6");

    private static string Example(Field f) => f.Type switch
    {
        FieldType.Date => "31.07.2026",
        FieldType.Number => "1234.56",
        _ => f.Name.Contains("Kode", StringComparison.OrdinalIgnoreCase) ? "912.001.006"
           : f.Name.Contains("Nama", StringComparison.OrdinalIgnoreCase) ? "BLEACHING EARTH"
           : "text"
    };

    public static (byte[] bytes, string fileName) Build(Catalog.Report report)
    {
        using var wb = new XLWorkbook();

        // ---------- sheet 1: the sheet to fill in ----------
        // Sheet name carries the period; the parser reads it (e.g. "… JULI 2026") for stock reports.
        var sheetName = SafeSheetName(report.Template == "WIP" ? "Laporan WIP" : report.Title);
        var ws = wb.Worksheets.Add(sheetName);

        ws.Cell(1, 1).Value = "No";
        for (int i = 0; i < report.Fields.Length; i++)
            ws.Cell(1, i + 2).Value = report.Fields[i].Name;

        var header = ws.Range(1, 1, 1, report.Fields.Length + 1);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = Navy;
        header.Style.Alignment.WrapText = false;
        ws.SheetView.FreezeRows(1);

        // text columns keep identifiers exactly as typed (leading zeros survive) — FR-I11
        for (int i = 0; i < report.Fields.Length; i++)
            if (report.Fields[i].Type == FieldType.Text)
                ws.Column(i + 2).Style.NumberFormat.Format = "@";
        ws.Columns(1, Math.Min(report.Fields.Length + 1, 40)).AdjustToContents(1, 1);
        foreach (var col in ws.Columns(1, report.Fields.Length + 1))
            if (col.Width > 34) col.Width = 34;

        // ---------- sheet 2: how to use it (never parsed) ----------
        var info = wb.Worksheets.Add("Petunjuk");
        info.Cell(1, 1).Value = report.Title;
        info.Cell(1, 1).Style.Font.Bold = true;
        info.Cell(1, 1).Style.Font.FontSize = 14;
        info.Cell(1, 1).Style.Font.FontColor = Navy;

        var lines = new[]
        {
            $"Template code: {report.Template}  ·  appears in: {(report.Page == "movement" ? "Inventory Movement" : "Reports")}",
            "",
            "How to use:",
            $"1. Enter your data on the '{sheetName}' sheet, one row per line item, starting at row 2.",
            "2. Do not rename, delete or reorder the header row — columns are matched by their header text.",
            "   (Reordering is tolerated, but renaming is not. Leave unused columns blank.)",
            "3. Dates: 31.07.2026 or a real Excel date cell. Numbers: plain values; thousand separators are fine.",
            "4. Codes and document numbers are text — leading zeros are preserved exactly as typed.",
            "5. Upload on the 'Ingestion & Upload' page. The template is recognised from these headers,",
            "   so the file name does not matter" +
                (report.Template is "BAHANBAKU" or "BARANGJADI"
                    ? " — except for this report: Bahan Baku and Barang Jadi have identical columns,"
                    : "."),
            report.Template is "BAHANBAKU" or "BARANGJADI"
                ? "   so keep the words 'Bahan Baku' / 'Barang Jadi' in the sheet or file name."
                : "",
            report.Page == "movement"
                ? "6. Keep the reporting month in the sheet name (e.g. 'JULI 2026') — it sets the period."
                : "",
            "",
            "Rows that fail validation are quarantined with a reason and shown back to you — never dropped.",
            "Re-uploading an identical file is rejected as a duplicate.",
            "",
            "Columns:"
        };
        int row = 3;
        foreach (var l in lines)
        {
            if (l.Length > 0) info.Cell(row, 1).Value = l;
            row++;
        }

        var hdr = row;
        info.Cell(hdr, 1).Value = "#";
        info.Cell(hdr, 2).Value = "Column header (must match)";
        info.Cell(hdr, 3).Value = "Shown in the app as";
        info.Cell(hdr, 4).Value = "Type";
        info.Cell(hdr, 5).Value = "Group";
        info.Cell(hdr, 6).Value = "Example";
        var ih = info.Range(hdr, 1, hdr, 6);
        ih.Style.Font.Bold = true;
        ih.Style.Font.FontColor = XLColor.White;
        ih.Style.Fill.BackgroundColor = Navy;

        info.Cell(hdr + 1, 1).Value = 1;
        info.Cell(hdr + 1, 2).Value = "No";
        info.Cell(hdr + 1, 3).Value = "row number (optional)";
        info.Cell(hdr + 1, 4).Value = "number";
        info.Cell(hdr + 1, 6).Value = "1";
        info.Row(hdr + 1).Style.Fill.BackgroundColor = Chip;

        for (int i = 0; i < report.Fields.Length; i++)
        {
            var f = report.Fields[i];
            var r = hdr + 2 + i;
            info.Cell(r, 1).Value = i + 2;
            info.Cell(r, 2).Value = f.Name;
            info.Cell(r, 3).Value = f.Display == f.Name ? "" : f.Display;
            info.Cell(r, 4).Value = f.Type.ToString().ToLowerInvariant();
            info.Cell(r, 5).Value = f.Group;
            info.Cell(r, 6).Value = Example(f);
            if (report.Defaults.Contains(f.Name)) info.Cell(r, 2).Style.Font.Bold = true;
        }
        info.Cell(hdr + 2 + report.Fields.Length + 1, 1).Value =
            "Bold column headers are the ones shown by default in the app.";
        info.Columns(1, 6).AdjustToContents();
        foreach (var col in info.Columns(1, 6))
            if (col.Width > 52) col.Width = 52;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return (ms.ToArray(), $"Template_{report.Template}_{report.Key}.xlsx");
    }

    private static string SafeSheetName(string s)
    {
        foreach (var ch in new[] { '\\', '/', '*', '?', ':', '[', ']' }) s = s.Replace(ch, ' ');
        s = s.Replace("—", "-").Trim();
        return s.Length > 31 ? s[..31].Trim() : s;
    }
}
