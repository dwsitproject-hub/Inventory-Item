using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace BcInventory.Api;

public record ParsedLine(int RowNo, Dictionary<string, object?> Data, List<string> Problems);

public record ParseResult(
    List<ParsedLine> Lines,
    Dictionary<string, string> HeaderMeta,
    Dictionary<string, string> FooterTotals);

public static class Ingestion
{
    // ---------- format sniffing (FR-I8): by content, never by extension ----------
    public static string SniffTemplate(byte[] bytes, string fileName = "")
    {
        if (XlsxParser.IsXlsx(bytes))
            return XlsxParser.SniffTemplate(bytes, fileName) ?? "UNKNOWN";

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (head.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return "BC40";
        if (head.Contains('\t') && head.Contains("PIB Report", StringComparison.OrdinalIgnoreCase)) return "BC23";
        if (head.Contains('\t')) return "BC23";
        return "UNKNOWN";
    }

    // ---------- BC23 / PIB import: tab-separated text, 80 columns ----------
    public static ParseResult ParseBc23(byte[] bytes)
    {
        var fields = Catalog.Pib;
        var text = Encoding.Latin1.GetString(bytes);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var meta = new Dictionary<string, string>();
        if (lines.Count > 0) meta["entity"] = First(lines[0]);
        if (lines.Count > 1) meta["report"] = First(lines[1]);
        if (lines.Count > 2) meta["period"] = First(lines[2]);

        // locate the column-header row ("Status\tNo.Pengajuan PIB\t...")
        int headerIdx = lines.FindIndex(l => l.StartsWith("Status\t"));
        if (headerIdx < 0) throw new InvalidDataException("BC23: header row not found");

        var result = new List<ParsedLine>();
        var footer = new Dictionary<string, string>();

        for (int i = headerIdx + 1; i < lines.Count; i++)
        {
            var cells = lines[i].Split('\t');
            if (cells.All(c => string.IsNullOrWhiteSpace(c))) continue;          // blank separator rows
            var first = cells[0].Trim();
            if (first == "Total")                                                // grand-total footer: checksum, not data
            {
                for (int c = 0; c < cells.Length; c++)
                    if (!string.IsNullOrWhiteSpace(cells[c])) footer[$"col{c}"] = Unwrap(cells[c].Trim());
                break;
            }

            var data = new Dictionary<string, object?>();
            var problems = new List<string>();
            for (int c = 0; c < fields.Length && c < cells.Length; c++)
            {
                var raw = Unwrap(cells[c].Trim());
                if (raw.Length == 0) continue;
                data[fields[c].Name] = Normalize(raw, fields[c].Type, problems, fields[c].Name);
            }
            if (data.Count == 0) continue;
            if (!data.ContainsKey("NoHS")) problems.Add("missing NoHS (mandatory for BC 2.3/PIB rows)");
            result.Add(new ParsedLine(i + 1, data, problems));
        }
        return new ParseResult(result, meta, footer);
    }

    // ---------- BC40: HTML table, 56 columns, 3 header rows, continuation rows ----------
    private static readonly Regex RowRx = new(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CellRx = new(@"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Compiled);

    public static ParseResult ParseBc40(byte[] bytes)
    {
        var fields = Catalog.Bc40;                 // 55 stored fields; source col 0 = "No" (sequence, not stored)
        var text = Encoding.Latin1.GetString(bytes);
        var result = new List<ParsedLine>();
        int rowIdx = 0;
        ParsedLine? current = null;

        // the legacy export repeats its 3-row header block on every "page" — recognise all variants (FR-I9)
        var headerTokens = new HashSet<string> { "Document BC", "BC \"Aju\"", "Price (excl PPN)", "Status KB", "Realization of Good Receipts" };

        foreach (Match m in RowRx.Matches(text))
        {
            rowIdx++;
            if (rowIdx <= 3) continue;             // merged 3-row header block
            var cells = CellRx.Matches(m.Groups[1].Value)
                .Select(c => WebUtility.HtmlDecode(TagRx.Replace(c.Groups[1].Value, " ")).Replace(' ', ' ').Trim())
                .ToArray();
            if (cells.Length == 0) continue;
            if (cells.Any(c => headerTokens.Contains(c))) continue;   // repeated per-page header rows

            bool isContinuation = string.IsNullOrWhiteSpace(cells[0]);
            if (isContinuation && current != null)
            {
                // continuation rows carry overflow text (e.g. long addresses) — fold into parent (FR-I9)
                for (int c = 1; c < cells.Length && c <= fields.Length; c++)
                {
                    if (string.IsNullOrWhiteSpace(cells[c])) continue;
                    var name = fields[c - 1].Name;
                    var prev = current.Data.TryGetValue(name, out var v) ? v?.ToString() : null;
                    current.Data[name] = string.IsNullOrEmpty(prev) ? cells[c] : prev + " " + cells[c];
                }
                continue;
            }
            if (isContinuation) continue;

            var data = new Dictionary<string, object?>();
            var problems = new List<string>();
            for (int c = 1; c < cells.Length && c <= fields.Length; c++)   // c=0 is the per-file row number
            {
                var raw = cells[c];
                if (raw.Length == 0) continue;
                var f = fields[c - 1];
                data[f.Name] = Normalize(raw, f.Type, problems, f.Name);
            }
            if (data.Count == 0) continue;
            current = new ParsedLine(rowIdx, data, problems);
            result.Add(current);
        }
        return new ParseResult(result, new Dictionary<string, string> { ["source"] = "Report output" },
            new Dictionary<string, string>());
    }

    // ---------- value normalisation (FR-I9/I11) ----------
    private static string Unwrap(string v) =>
        v.StartsWith("=\"") && v.EndsWith("\"") ? v[2..^1] : v;   // ="0091" text guard -> 0091, zeros preserved

    /// <summary>Shared with <see cref="XlsxParser"/> so every template normalises values identically.</summary>
    public static object? NormalizeValue(string raw, FieldType type, List<string> problems, string name) =>
        Normalize(raw, type, problems, name);

    private static object? Normalize(string raw, FieldType type, List<string> problems, string name)
    {
        switch (type)
        {
            case FieldType.Number:
                // legacy exports pad negatives ("-  32,029,167") and suffix rates ("0.00 %")
                var cleaned = raw.Replace(",", "").Replace("%", "").Replace(" ", "").Trim();
                if (cleaned.Length == 0 || cleaned == "-") return null;
                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d;
                problems.Add($"'{name}': not a number ('{raw}')");
                return raw;
            case FieldType.Date:
                // legacy extracts use dd.MM.yyyy; xlsx date cells arrive already ISO-formatted
                if (DateOnly.TryParseExact(raw, "dd.MM.yyyy", out var dt) ||
                    DateOnly.TryParseExact(raw, "yyyy-MM-dd", out dt) ||
                    DateOnly.TryParseExact(raw, "dd/MM/yyyy", out dt))
                    return dt.ToString("yyyy-MM-dd");
                problems.Add($"'{name}': not a recognised date ('{raw}')");
                return raw;
            default:
                return raw;
        }
    }

    private static string First(string line) => line.Split('\t')[0].Trim();

    // ---------- loader: staging-free MVP upsert, idempotent per file hash (FR-I6) ----------
    public static async Task<object> Load(NpgsqlDataSource ds, string fileName, byte[] bytes, string source, string? uploadedBy)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var con = await ds.OpenConnectionAsync();

        var dup = await con.ExecuteScalarAsync<long?>(
            "select id from ingest.ingestion_files where file_hash = @hash", new { hash });
        if (dup != null)
        {
            Audit.Log("ingest.duplicate", null, "file", fileName, $"Rejected as duplicate of ingestion #{dup}",
                new { source, hash }, actorEmailOverride: uploadedBy);
            return new { status = "duplicate", message = $"File already ingested (id {dup})." };
        }

        string template;
        try
        {
            template = SniffTemplate(bytes, fileName);
        }
        catch (InvalidDataException ex)
        {
            // e.g. a layout shared by two reports with nothing in the name to separate them
            Audit.Log("ingest.rejected", null, "file", fileName, ex.Message, null, actorEmailOverride: uploadedBy);
            return new { status = "rejected", message = ex.Message };
        }
        if (template == "UNKNOWN")
            return new
            {
                status = "rejected",
                message = "Unrecognised template/format (FR-I8). Supported: BC23 (TSV), BC40 (HTML table), " +
                          "and the xlsx templates WIP, Bahan Baku, Barang Jadi, Aset dan Sparepart, BC 3.0."
            };

        ParseResult parsed;
        try
        {
            // Any real workbook goes through the xlsx path — including BC23/BC40 filled in on
            // a downloaded template. The legacy TSV/HTML parsers only handle the SAP exports.
            parsed = XlsxParser.IsXlsx(bytes)
                ? XlsxParser.Parse(bytes, fileName)
                : template switch
                {
                    "BC23" => ParseBc23(bytes),
                    "BC40" => ParseBc40(bytes),
                    _ => throw new InvalidDataException($"No non-xlsx parser for template {template}.")
                };
        }
        catch (Exception ex)
        {
            await con.ExecuteAsync("""
                insert into ingest.ingestion_files (file_name, file_hash, file_size, source, template, status, error, uploaded_by)
                values (@fileName, @hash, @size, @source, @template, 'failed', @err, @uploadedBy)
                """, new { fileName, hash, size = (long)bytes.Length, source, template, err = ex.Message, uploadedBy });
            await Notifications.Emit(con, "error", $"Ingestion failed — {fileName}", ex.Message,
                new[] { "Super Admin", "Admin", "Data Steward" });
            Audit.Log("ingest.failed", null, "file", fileName, $"Parse failed: {ex.Message}",
                new { source, template, hash }, actorEmailOverride: uploadedBy);
            return new { status = "failed", message = ex.Message };
        }

        var good = parsed.Lines.Where(l => l.Problems.Count == 0).ToList();
        var bad = parsed.Lines.Where(l => l.Problems.Count > 0).ToList();

        await using var tx = await con.BeginTransactionAsync();

        var fileId = await con.ExecuteScalarAsync<long>("""
            insert into ingest.ingestion_files
                (file_name, file_hash, file_size, source, template, status, rows_total, rows_loaded, rows_quarantined, header_meta, footer_totals, uploaded_by)
            values (@fileName, @hash, @size, @source, @template, @status, @total, @loaded, @quarantined, @meta::jsonb, @footer::jsonb, @uploadedBy)
            returning id
            """, new
        {
            fileName, hash, size = (long)bytes.Length, source, template,
            status = bad.Count == 0 ? "loaded" : "partial",
            total = parsed.Lines.Count, loaded = good.Count, quarantined = bad.Count,
            meta = JsonSerializer.Serialize(parsed.HeaderMeta),
            footer = JsonSerializer.Serialize(parsed.FooterTotals),
            uploadedBy
        }, tx);

        // resolve scope (MVP: single seeded entity; site/TPB resolved from BC40 rows)
        var entityId = await con.ExecuteScalarAsync<long>("select id from master.entities order by id limit 1", transaction: tx);
        var siteCache = new Dictionary<string, long>();
        var tpbCache = new Dictionary<string, long>();
        var docCache = new Dictionary<string, long>();
        int lineNoFallback = 0;

        // reporting period for the stock/mutation templates (from the sheet name, e.g. "JULI 2026")
        var periodIso = parsed.HeaderMeta.GetValueOrDefault("period");
        var periodKey = periodIso is null
            ? Path.GetFileNameWithoutExtension(fileName)
            : periodIso[..7];                       // yyyy-MM

        foreach (var line in good)
        {
            string aju, docNo, docType; string? docDateIso; long? siteId = null, tpbId = null;
            if (template == "BC23")
            {
                aju = Str(line, "No.Pengajuan PIB");
                docNo = Str(line, "No. PIB");
                docType = Str(line, "Tipe PIB");
                docDateIso = StrOrNull(line, "PibDate");
            }
            else if (template == "BC30")
            {
                aju = "";
                docNo = Str(line, "Dok. Pabean / Nomor");
                docType = Str(line, "Jenis Dok.");
                docDateIso = StrOrNull(line, "Dok. Pabean / Tanggal");
            }
            else if (template is "WIP" or "BAHANBAKU" or "BARANGJADI" or "ASET" or "SCRAP")
            {
                // Periodic stock/mutation reports carry no customs document: one document per
                // reporting period, so re-uploading a corrected period updates in place (FR-I6).
                aju = "";
                docNo = periodKey;
                docType = template;
                docDateIso = periodIso;
            }
            else
            {
                aju = Str(line, "BC \"Aju\" / No");
                docNo = Str(line, "Document BC / No");
                docType = Str(line, "Type");
                docDateIso = StrOrNull(line, "Document BC / Date");

                var siteName = Str(line, "Location");
                if (siteName.Length > 0 && !siteCache.TryGetValue(siteName, out _))
                    siteCache[siteName] = await con.ExecuteScalarAsync<long>("""
                        insert into master.sites (entity_id, name) values (@e, @n)
                        on conflict (entity_id, name) do update set name = excluded.name returning id
                        """, new { e = entityId, n = siteName }, tx);
                if (siteName.Length > 0) siteId = siteCache[siteName];

                var permit = Str(line, "TPB No.");
                if (permit.Length > 0 && !tpbCache.TryGetValue(permit, out _))
                    tpbCache[permit] = await con.ExecuteScalarAsync<long>("""
                        insert into master.tpb_permits (entity_id, site_id, permit_no) values (@e, @s, @p)
                        on conflict (permit_no) do update set permit_no = excluded.permit_no returning id
                        """, new { e = entityId, s = siteId, p = permit }, tx);
                if (permit.Length > 0) tpbId = tpbCache[permit];
            }

            var docKey = aju + "" + docNo;
            if (!docCache.TryGetValue(docKey, out var docId))
            {
                docId = await con.ExecuteScalarAsync<long>("""
                    insert into bc.documents (template, doc_type, aju_number, doc_number, doc_date, entity_id, site_id, tpb_id, supplier_name, ingestion_file_id)
                    values (@template, @docType, @aju, @docNo, @docDate::date, @entityId, @siteId, @tpbId, @supplier, @fileId)
                    on conflict (template, aju_number, doc_number) do update set doc_type = excluded.doc_type
                    returning id
                    """, new
                {
                    template, docType, aju, docNo, docDate = docDateIso, entityId, siteId, tpbId,
                    supplier = template switch
                    {
                        "BC23" => StrOrNull(line, "Supplier Name"),
                        "BC40" => StrOrNull(line, "Vendor Name"),
                        "BC30" => StrOrNull(line, "Penerima / Pembeli"),
                        _ => null                       // stock reports have no counterparty
                    },
                    fileId
                }, tx);
                docCache[docKey] = docId;
            }

            var cmd = new NpgsqlCommand("""
                insert into bc.document_lines (document_id, template, doc_type, doc_date, entity_id, site_id, tpb_id, line_no, data, ingestion_file_id)
                values (@doc, @template, @docType, @docDate::date, @entityId, @siteId, @tpbId,
                        coalesce((select max(line_no) from bc.document_lines where document_id = @doc), 0) + 1,
                        @data, @fileId)
                """, con, tx);
            cmd.Parameters.AddWithValue("doc", docId);
            cmd.Parameters.AddWithValue("template", template);
            cmd.Parameters.AddWithValue("docType", (object?)docType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("docDate", (object?)docDateIso ?? DBNull.Value);
            cmd.Parameters.AddWithValue("entityId", entityId);
            cmd.Parameters.AddWithValue("siteId", (object?)siteId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tpbId", (object?)tpbId ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(line.Data) });
            cmd.Parameters.AddWithValue("fileId", fileId);
            await cmd.ExecuteNonQueryAsync();
            lineNoFallback++;
        }

        foreach (var q in bad)
            await con.ExecuteAsync("""
                insert into ingest.quarantine_rows (ingestion_file_id, row_no, raw_data, reasons)
                values (@fileId, @rowNo, @raw::jsonb, @reasons)
                """, new
            {
                fileId, rowNo = q.RowNo,
                raw = JsonSerializer.Serialize(q.Data),
                reasons = q.Problems.ToArray()
            }, tx);

        await tx.CommitAsync();

        // alert events (FR-N1): upload to everyone in scope; quarantine detail to stewards/admins
        var summary = $"{template}: {good.Count:N0}/{parsed.Lines.Count:N0} rows loaded"
                      + (bad.Count > 0 ? $", {bad.Count:N0} quarantined" : "") + $" · source: {source}";
        await Notifications.Emit(con, "upload", $"New file ingested — {fileName}", summary);
        if (bad.Count > 0)
            await Notifications.Emit(con, "quarantine", $"Rows quarantined — {fileName}",
                $"{bad.Count:N0} row(s) failed validation and need review (Ingestion page).",
                new[] { "Super Admin", "Admin", "Data Steward" });

        Audit.Log("ingest.load", null, "file", fileName, summary,
            new { ingestionId = fileId, template, source, hash, rowsTotal = parsed.Lines.Count, rowsLoaded = good.Count, rowsQuarantined = bad.Count },
            actorEmailOverride: uploadedBy);

        return new
        {
            status = bad.Count == 0 ? "loaded" : "partial",
            ingestionId = fileId, template,
            rowsTotal = parsed.Lines.Count, rowsLoaded = good.Count, rowsQuarantined = bad.Count,
            headerMeta = parsed.HeaderMeta
        };
    }

    private static string Str(ParsedLine l, string k) => l.Data.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
    private static string? StrOrNull(ParsedLine l, string k) { var s = Str(l, k); return s.Length == 0 ? null : s; }

    // ---------- auto-ingest the real sample extracts at startup ----------
    public static async Task AutoIngestSamples(NpgsqlDataSource ds, string dir)
    {
        if (!Directory.Exists(dir)) { Console.WriteLine($"[samples] dir not found: {dir}"); return; }
        var samples = new[]
        {
            "BC23 Report.xls", "BC40 Report.xls",
            "Laporan BC3.0.xlsx", "Laporan WIP.xlsx",
            "Laporan Bahan Baku.xlsx", "Laporan Barang Jadi.xlsx", "Laporan Aset dan Sparepart.xlsx",
            "Laporan Scraps  Pusat Logistik Berikat PERIODE 01.01.2019 SD 11.08.2026.xlsx"
        };
        foreach (var name in samples)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var bytes = await File.ReadAllBytesAsync(path);
            var res = await Load(ds, name, bytes, "sample", "system@startup");
            Console.WriteLine($"[samples] {name}: {JsonSerializer.Serialize(res)}");
        }
    }
}
