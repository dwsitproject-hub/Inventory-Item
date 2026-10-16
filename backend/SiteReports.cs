using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <param name="Keys">Reports the site sees. Per page, none ticked means every report on it.</param>
/// <param name="Columns">
/// Per report, the columns the site sees. A report left out — or sent with exactly its starter
/// set — uses the starter set: every column except the opt-in ones.
/// </param>
public record SiteReportsRequest(string[]? Keys, Dictionary<string, string[]>? Columns = null);

/// <summary>
/// Which reports — and which columns of each — a site sees on Reports and Inventory Movement
/// (Admin → Master Data).
///
/// Reports are kept per page: a site with nothing ticked for a page sees every report on that
/// page, so a newly added site — or one nobody has configured yet — keeps today's behaviour,
/// and hiding a whole page stays the job of Role Management. Columns start from the report's
/// starter set (every column that is not <see cref="Field.OptIn"/>); an administrator may hide
/// some or switch opt-in ones on.
///
/// A user linked to several sites sees what any of them shows; an all-sites user (HO, Super
/// Admin) is never narrowed and sees every column. Enforced
/// server-side: the catalog marks a hidden report as not browsable and a hidden column as not
/// visible, and query / export / delete refuse a hidden report or column outright.
/// </summary>
public static class SiteReports
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    internal record Snapshot(Dictionary<long, HashSet<string>> Reports, Dictionary<(long site, string key), HashSet<string>> Columns);

    private static NpgsqlDataSource? _ds;
    private static Snapshot _snap = new(new(), new());
    private static DateTime _loadedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static void Configure(NpgsqlDataSource ds) => _ds = ds;

    /// <summary>Reports a site may be narrowed to: the ones offered in a page's report picker.</summary>
    public static IEnumerable<Catalog.Report> Assignable =>
        Catalog.Reports.Where(r => r.Browse && r.Page is "reports" or "movement");

    /// <summary>The columns a configured site starts from: all but the opt-in ones.</summary>
    public static IEnumerable<string> Starter(Catalog.Report r) => r.Fields.Where(f => !f.OptIn).Select(f => f.Name);

    private static async Task<Snapshot> Load()
    {
        if (_ds is null || DateTime.UtcNow - _loadedAt < Ttl) return _snap;
        await _lock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _loadedAt < Ttl) return _snap;
            await using var con = await _ds.OpenConnectionAsync();
            var reports = await con.QueryAsync<(long siteId, string key)>(
                "select site_id, report_key from master.site_reports");
            var columns = await con.QueryAsync<(long siteId, string key, string[] cols)>(
                "select site_id, report_key, columns from master.site_report_columns");
            // Build, then swap the reference, so a reader never sees a half-filled snapshot.
            _snap = new Snapshot(
                reports.GroupBy(r => r.siteId).ToDictionary(g => g.Key, g => g.Select(r => r.key).ToHashSet()),
                columns.ToDictionary(c => (c.siteId, c.key), c => c.cols.ToHashSet()));
            _loadedAt = DateTime.UtcNow;
            return _snap;
        }
        finally { _lock.Release(); }
    }

    private static void Invalidate() => _loadedAt = DateTime.MinValue;

    /// <summary>
    /// What one user's sites may see: the union over their sites — a report or column any of
    /// them shows is shown. Unrestricted for an all-sites user (and when no site is assigned,
    /// since such a user sees no rows anyway).
    /// </summary>
    public sealed class View
    {
        private readonly long[] _sites;
        private readonly Snapshot _snap;

        internal View(long[] sites, Snapshot snap) { _sites = sites; _snap = snap; }

        private bool SiteSees(long site, Catalog.Report r)
        {
            if (!_snap.Reports.TryGetValue(site, out var keys)) return true;
            // Per page: nothing configured for this page means the whole page is visible.
            return keys.Contains(r.Key) || !Catalog.Reports.Any(x => x.Page == r.Page && keys.Contains(x.Key));
        }

        public bool Sees(Catalog.Report r) => _sites.Length == 0 || _sites.Any(s => SiteSees(s, r));

        /// <summary>Column names this user may see, or null for every column.</summary>
        public HashSet<string>? Columns(Catalog.Report r)
        {
            if (_sites.Length == 0) return null;
            var cols = new HashSet<string>();
            foreach (var s in _sites.Where(s => SiteSees(s, r)))
                cols.UnionWith(_snap.Columns.TryGetValue((s, r.Key), out var c) ? c : Starter(r));
            return cols.Count > 0 ? cols : Starter(r).ToHashSet();
        }
    }

    public static async Task<View> For(UserScope scope) =>
        new(scope.AllSites ? Array.Empty<long>() : scope.SiteIds, await Load());

    /// <summary>Refuse a report key the user's site is not configured to see.</summary>
    public static IResult? Require(View view, string key)
    {
        var report = Catalog.Get(key);
        if (report is null) return null;                 // unknown key: the handler answers 404
        return view.Sees(report) ? null
            : Results.Problem(statusCode: 403, title: "SCOPE-004",
                detail: $"{report.Title} is not enabled for your site. An administrator can enable it under Administration → Master Data.");
    }

    /// <summary>Report keys and column lists configured per site, for the Master Data tab.</summary>
    public static async Task<(Dictionary<long, string[]> reports, Dictionary<long, Dictionary<string, string[]>> columns)> All(NpgsqlConnection con)
    {
        var reports = (await con.QueryAsync<(long siteId, string key)>("select site_id, report_key from master.site_reports order by report_key"))
            .GroupBy(r => r.siteId).ToDictionary(g => g.Key, g => g.Select(r => r.key).ToArray());
        var columns = (await con.QueryAsync<(long siteId, string key, string[] cols)>("select site_id, report_key, columns from master.site_report_columns"))
            .GroupBy(c => c.siteId).ToDictionary(g => g.Key, g => g.ToDictionary(c => c.key, c => c.cols));
        return (reports, columns);
    }

    public static async Task<IResult> Set(NpgsqlDataSource ds, UserScope scope, long siteId, SiteReportsRequest req)
    {
        if (await Admin.RequireAdmin(scope, "edit") is { } err) return err;
        var keys = (req.Keys ?? Array.Empty<string>()).Distinct().ToArray();
        var assignable = Assignable.ToDictionary(r => r.Key);
        if (keys.FirstOrDefault(k => !assignable.ContainsKey(k)) is { } bad)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"Unknown report '{bad}'.");

        // Keep only lists that differ from the starter set, kept in catalog order, so a later
        // change to a report's starter columns reaches every site that never customised it.
        var columns = new List<(string key, string[] cols)>();
        foreach (var (key, raw) in req.Columns ?? new())
        {
            if (!assignable.TryGetValue(key, out var report))
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"Unknown report '{key}'.");
            var wanted = (raw ?? Array.Empty<string>()).ToHashSet();
            if (wanted.FirstOrDefault(c => report.Fields.All(f => f.Name != c)) is { } badCol)
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"{report.Title} has no column '{badCol}'.");
            if (wanted.Count == 0)
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"{report.Title} needs at least one column.");
            if (wanted.SetEquals(Starter(report))) continue;
            columns.Add((key, report.Fields.Select(f => f.Name).Where(wanted.Contains).ToArray()));
        }

        await using var con = await ds.OpenConnectionAsync();
        var site = await con.QueryFirstOrDefaultAsync<string?>("select name from master.sites where id = @siteId", new { siteId });
        if (site is null) return Results.Problem(statusCode: 404, title: "VAL-003", detail: "Site not found.");

        await using var tx = await con.BeginTransactionAsync();
        await con.ExecuteAsync("delete from master.site_reports where site_id = @siteId", new { siteId }, tx);
        if (keys.Length > 0)
            await con.ExecuteAsync(
                "insert into master.site_reports (site_id, report_key) select @siteId, unnest(@keys::text[])",
                new { siteId, keys }, tx);
        await con.ExecuteAsync("delete from master.site_report_columns where site_id = @siteId", new { siteId }, tx);
        foreach (var (key, cols) in columns)
            await con.ExecuteAsync(
                "insert into master.site_report_columns (site_id, report_key, columns) values (@siteId, @key, @cols)",
                new { siteId, key, cols }, tx);
        await tx.CommitAsync();
        Invalidate();

        Audit.Log("master.site.reports", scope, "site", siteId.ToString(),
            (keys.Length == 0 ? $"Site {site}: all reports visible" : $"Site {site}: {keys.Length} report(s) configured")
            + (columns.Count == 0 ? "" : $", custom columns on {columns.Count} report(s)"),
            new { keys, columns = columns.ToDictionary(c => c.key, c => c.cols) });
        return Results.Ok(new { siteId, keys });
    }
}
