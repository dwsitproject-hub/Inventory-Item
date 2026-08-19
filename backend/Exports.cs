using System.Text;
using ClosedXML.Excel;
using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <summary>
/// Exports honour the grid's exact request shape — visible columns, order and sort (FR-R5/R13).
/// Synchronous for the MVP (capped at 100k rows); async job + notification at build-out (TechDoc D6).
/// </summary>
public static class Exports
{
    private const int MaxRows = 100_000;

    public static async Task<IResult> Run(NpgsqlDataSource ds, string key, string format, QueryRequest req, UserScope scope, string? ip = null)
    {
        var (built, error) = Reports.Build(key, req, scope);
        if (error != null) return error;
        var b = built!;
        var byName = b.Report.Fields.ToDictionary(f => f.Name);

        var proj = string.Join(", ", b.Columns.Select((c, i) => $"{Reports.Expr(byName[c])} as c{i}"));
        var sql = $"""
            select {proj}
            from bc.document_lines l
            where {b.Where}
            order by {b.Order}
            limit {MaxRows}
            """;

        await using var con = await ds.OpenConnectionAsync();
        var raw = (await con.QueryAsync(sql, b.Params)).Cast<IDictionary<string, object?>>().ToList();

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmm");
        var baseName = $"{key}_{stamp}";

        // exporting data leaves the system — always audited (FR-A7)
        Audit.Log("report.export", scope, "report", key,
            $"Exported {Fmt.N(raw.Count)} rows from {b.Report.Title} as {format.ToLowerInvariant()}",
            new { format, columns = b.Columns, sort = req.Sort, filters = req.Filters, rows = raw.Count }, ip);

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", b.Columns.Select(c => Quote(byName[c].Display))));
            foreach (var r in raw)
                sb.AppendLine(string.Join(",", b.Columns.Select((c, i) => Quote(Cell(r[$"c{i}"])))));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv", baseName + ".csv");
        }

        if (format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(b.Report.Template);
            for (int c = 0; c < b.Columns.Length; c++)
            {
                var hc = ws.Cell(1, c + 1);
                hc.Value = byName[b.Columns[c]].Display;
                hc.Style.Font.Bold = true;
                hc.Style.Font.FontColor = XLColor.White;
                hc.Style.Fill.BackgroundColor = XLColor.FromHtml("#0E2841");
            }
            for (int rIdx = 0; rIdx < raw.Count; rIdx++)
            {
                var r = raw[rIdx];
                for (int c = 0; c < b.Columns.Length; c++)
                {
                    var cell = ws.Cell(rIdx + 2, c + 1);
                    var v = r[$"c{c}"];
                    var f = byName[b.Columns[c]];
                    switch (v)
                    {
                        case null: break;
                        case decimal d: cell.Value = d; cell.Style.NumberFormat.Format = "#,##0.###"; break;
                        case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "dd/mm/yyyy"; break;
                        default:
                            // identifiers stay text — leading zeros preserved (FR-I11)
                            cell.SetValue(v.ToString());
                            if (f.Type == FieldType.Text) cell.Style.NumberFormat.Format = "@";
                            break;
                    }
                }
            }
            ws.SheetView.FreezeRows(1);
            ws.Columns(1, Math.Min(b.Columns.Length, 30)).AdjustToContents(1, Math.Min(raw.Count + 1, 200));
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Results.File(ms.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", baseName + ".xlsx");
        }

        return Results.Problem(statusCode: 400, title: "VAL-001", detail: "format must be csv or xlsx.");
    }

    private static string Cell(object? v) => v switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd"),
        _ => v.ToString() ?? ""
    };

    private static string Quote(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
