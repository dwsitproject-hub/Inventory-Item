using System.Text;
using Dapper;
using Npgsql;

namespace BcInventory.Api;

public record SortSpec(string Field, string Dir);
public record PageSpec(int Size, int Offset);

public record QueryRequest(
    Dictionary<string, string?>? Filters,
    string[]? Columns,
    SortSpec[]? Sort,
    PageSpec? Page);

/// <summary>
/// Delete request (FR-R14). Mode "ids" removes the listed rows; mode "filter" removes every row
/// matching the same filter the grid is showing. Both are bounded to the caller's entity scope.
/// </summary>
public record DeleteRequest(
    string Mode,                                // "ids" | "filter"
    long[]? Ids,
    Dictionary<string, string?>? Filters);

public record BuiltQuery(Catalog.Report Report, string Where, DynamicParameters Params, string Order, string[] Columns);

/// <summary>
/// Server-side report query: column projection + sorting + paging, scope-bounded (TechDoc §4.3).
/// Field names are the exact upload headers; whitelisted against the catalog — never concatenated from user text.
/// </summary>
public static class Reports
{
    public static object CatalogList() => Catalog.Reports.Select(r => new
    {
        key = r.Key,
        title = r.Title,
        template = r.Template,
        page = r.Page,
        upload = r.Upload,                              // owns an upload template
        browse = r.Browse,                              // offered in the report picker
        defaults = r.Defaults,
        fields = r.Fields.Select(f => new
        {
            name = f.Name,                              // verbatim upload header — the stable key
            label = f.Display,                          // what the user sees
            type = f.Type.ToString().ToLowerInvariant(),
            group = f.Group
        })
    });

    public static (BuiltQuery? built, IResult? error) Build(string key, QueryRequest req, UserScope scope)
    {
        var report = Catalog.Get(key);
        if (report is null)
            // 404, matching the template endpoint: an unknown key is a missing resource, not a
            // malformed request. The two endpoints used to disagree (finding F-01).
            return (null, Results.Problem(statusCode: 404, title: "RPT-001", detail: $"Unknown report key '{key}'."));

        var byName = report.Fields.ToDictionary(f => f.Name);
        var columns = (req.Columns is { Length: > 0 } ? req.Columns : report.Defaults).Distinct().ToArray();
        foreach (var c in columns)
            if (!byName.ContainsKey(c))
                return (null, Results.Problem(statusCode: 400, title: "RPT-001", detail: $"Unknown column '{c}'."));

        var sorts = req.Sort ?? Array.Empty<SortSpec>();
        foreach (var s in sorts)
            if (!byName.ContainsKey(s.Field))
                return (null, Results.Problem(statusCode: 400, title: "RPT-001", detail: $"Unknown sort field '{s.Field}'."));

        // A report may draw its rows from a predicate rather than a bare template match — one
        // uploaded Aset dan Sparepart file is read as Sparepart, Aset and part of Bahan Baku.
        var where = new StringBuilder(report.Where ?? "l.template = @template");
        var p = new DynamicParameters();
        p.Add("template", report.Template);

        // scope: blank filter = all within MY scope, never all rows (FR-R2a)
        if (!scope.AllEntities)
        {
            where.Append(" and l.entity_id = @scopeEntity");
            p.Add("scopeEntity", scope.EntityId);
        }
        var f = req.Filters ?? new();
        if (f.TryGetValue("entityId", out var entS) && long.TryParse(entS, out var ent))
        {
            if (!scope.AllEntities && ent != scope.EntityId)
                return (null, Results.Problem(statusCode: 403, title: "SCOPE-001", detail: "Requested entity outside your scope."));
            where.Append(" and l.entity_id = @ent"); p.Add("ent", ent);
        }
        if (f.TryGetValue("dateFrom", out var df) && DateOnly.TryParse(df, out var dFrom))
        { where.Append(" and l.doc_date >= @dFrom"); p.Add("dFrom", dFrom); }
        if (f.TryGetValue("dateTo", out var dt) && DateOnly.TryParse(dt, out var dTo))
        { where.Append(" and l.doc_date <= @dTo"); p.Add("dTo", dTo); }
        if (f.TryGetValue("search", out var q) && !string.IsNullOrWhiteSpace(q))
        {
            var ors = report.SearchFields.Select(sf => $"l.data->>'{Sql(sf)}' ilike @search");
            where.Append(" and (").Append(string.Join(" or ", ors)).Append(')');
            p.Add("search", "%" + q.Trim() + "%");
        }

        var order = sorts.Length == 0
            ? "l.doc_date desc nulls last, l.id"
            : string.Join(", ", sorts.Select(s => $"{Expr(byName[s.Field])} {(s.Dir?.ToLowerInvariant() == "desc" ? "desc" : "asc")} nulls last")) + ", l.id";

        return (new BuiltQuery(report, where.ToString(), p, order, columns), null);
    }

    public static string Expr(Field fld) => fld.Type switch
    {
        FieldType.Number => $"nullif(l.data->>'{Sql(fld.Name)}','')::numeric",
        FieldType.Date => $"nullif(l.data->>'{Sql(fld.Name)}','')::date",
        _ => $"l.data->>'{Sql(fld.Name)}'"
    };

    public static async Task<IResult> Query(NpgsqlDataSource ds, string key, QueryRequest req, UserScope scope, string? ip = null)
    {
        var (built, error) = Build(key, req, scope);
        if (error != null) return error;
        var b = built!;
        var byName = b.Report.Fields.ToDictionary(f => f.Name);

        var size = Math.Clamp(req.Page?.Size ?? 25, 1, 200);
        var offset = Math.Max(req.Page?.Offset ?? 0, 0);
        var proj = string.Join(", ", b.Columns.Select((c, i) => $"{Expr(byName[c])} as c{i}"));

        // The line id travels with each row so the grid can offer per-row delete (FR-R14). It is
        // an internal key, returned under a reserved name the column projection can never collide with.
        var sql = $"""
            select l.id as __id, {proj}
            from bc.document_lines l
            where {b.Where}
            order by {b.Order}
            limit {size} offset {offset}
            """;

        await using var con = await ds.OpenConnectionAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raw = (await con.QueryAsync(sql, b.Params)).Cast<IDictionary<string, object?>>().ToList();
        var total = await con.ExecuteScalarAsync<long>(
            $"select count(*) from (select 1 from bc.document_lines l where {b.Where} limit 100001) t", b.Params);
        var asOf = await con.QueryFirstOrDefaultAsync<(DateTime? at, string? file)>(
            "select max(received_at), (array_agg(file_name order by received_at desc))[1] from ingest.ingestion_files where template = @template and status in ('loaded','partial')",
            new { template = b.Report.Template });
        sw.Stop();

        var rows = raw.Select(r =>
        {
            var o = new Dictionary<string, object?>();
            o["__id"] = r["__id"];
            for (int i = 0; i < b.Columns.Length; i++) o[b.Columns[i]] = r[$"c{i}"];
            return o;
        });

        Audit.Log("report.query", scope, "report", key,
            $"Ran {b.Report.Title} — {raw.Count} of {total} rows",
            new { filters = req.Filters, columns = b.Columns, sort = req.Sort, size, offset, elapsedMs = sw.ElapsedMilliseconds }, ip);

        return Results.Ok(new
        {
            data = new { rows },
            meta = new
            {
                total,
                page = new { size, offset },
                asOf = asOf.at,
                sourceFile = asOf.file,
                elapsedMs = sw.ElapsedMilliseconds
            }
        });
    }

    /// <summary>
    /// Permanently delete ingested rows (FR-R14). The caller must already hold the delete
    /// permission for the report's page (checked at the route). Deletion is bounded to the
    /// caller's entity scope by the same query builder the grid uses, so a scoped user can never
    /// reach another entity's rows even by passing ids. Every deletion is captured in the audit
    /// trail with a bounded snapshot of what was removed, so an append-only record survives.
    /// </summary>
    public static async Task<IResult> Delete(NpgsqlDataSource ds, string key, DeleteRequest req, UserScope scope, string? ip = null)
    {
        var mode = (req.Mode ?? "").ToLowerInvariant();
        if (mode is not ("ids" or "filter"))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "mode must be 'ids' or 'filter'.");

        // Reuse the query builder for the WHERE and the scope bound. Columns/sort are irrelevant here.
        var (built, error) = Build(key, new QueryRequest(req.Filters, null, null, null), scope);
        if (error != null) return error;
        var b = built!;
        var where = b.Where;
        var p = b.Params;

        if (mode == "ids")
        {
            var ids = (req.Ids ?? Array.Empty<long>()).Distinct().ToArray();
            if (ids.Length == 0)
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: "No rows selected.");
            if (ids.Length > 10000)
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Select at most 10,000 rows per delete.");
            where = $"({where}) and l.id = any(@ids)";     // still scope-bounded by the built WHERE
            p.Add("ids", ids);
        }

        await using var con = await ds.OpenConnectionAsync();
        await using var tx = await con.BeginTransactionAsync();

        // Snapshot before deleting: full count and ids, plus a small sample of row data so the
        // audit entry is human-readable without being unbounded.
        var affected = (await con.QueryAsync<(long id, long docId)>(
            $"select l.id, l.document_id from bc.document_lines l where {where}", p, tx)).ToList();
        if (affected.Count == 0)
            return Results.Problem(statusCode: 404, title: "RPT-003", detail: "No matching rows to delete (they may already be gone).");

        var sample = (await con.QueryAsync<string>(
            $"select l.data::text from bc.document_lines l where {where} order by l.id limit 20", p, tx)).ToList();

        var deleted = await con.ExecuteAsync($"delete from bc.document_lines l where {where}", p, tx);

        // Remove parent documents left with no lines, so document counts and the dashboard stay honest.
        var docIds = affected.Select(a => a.docId).Distinct().ToArray();
        var docsRemoved = await con.ExecuteAsync("""
            delete from bc.documents d
            where d.id = any(@docIds)
              and not exists (select 1 from bc.document_lines l where l.document_id = d.id)
            """, new { docIds }, tx);

        await tx.CommitAsync();

        var report = Catalog.Get(key)!;
        Audit.Log("report.delete", scope, "report", key,
            $"Deleted {deleted} row(s) from {report.Title}" + (docsRemoved > 0 ? $" ({docsRemoved} document(s) removed)" : ""),
            new
            {
                mode,
                report = report.Title,
                template = report.Template,
                filters = req.Filters,
                count = deleted,
                documentsRemoved = docsRemoved,
                ids = affected.Select(a => a.id).Take(5000).ToArray(),
                sample = sample.Select(s => System.Text.Json.JsonDocument.Parse(s).RootElement).ToArray(),
            }, ip);

        return Results.Ok(new { deleted, documentsRemoved = docsRemoved });
    }

    /// <summary>
    /// Quote a catalogue field name for embedding in SQL. The whitelist against the report's
    /// own fields is the real control; this is the second line (AR-09). It refuses anything
    /// that does not look like a field name rather than trying to sanitise it, so a code path
    /// that ever forgets the whitelist fails loudly instead of building injectable SQL.
    /// </summary>
    public static string Sql(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("Field name out of range.", nameof(name));
        foreach (var c in name)
            if (!(char.IsLetterOrDigit(c) || c is ' ' or '.' or '/' or '-' or '_' or '(' or ')' or '%' or '"' or '\''))
                throw new ArgumentException($"Field name contains an unexpected character: {name}", nameof(name));
        return name.Replace("'", "''");
    }
}
