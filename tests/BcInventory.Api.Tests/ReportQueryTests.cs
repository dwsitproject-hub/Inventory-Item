using Xunit;
using static BcInventory.Api.Tests.TestData;

namespace BcInventory.Api.Tests;

/// <summary>The report query builder: row scope, the site filter, and hidden-column refusal.</summary>
public class ReportQueryTests
{
    private const string Asal = "Dokumen Asal Barang / No Daftar";

    private static QueryRequest Req(string[]? columns = null, Dictionary<string, string?>? filters = null, SortSpec[]? sort = null) =>
        new(filters, columns, sort, null);

    [Fact]
    public void Site_user_rows_are_bound_to_their_companies_and_sites()
    {
        var (b, err) = Reports.Build("bc30-export", Req(), SiteUser(new long[] { 1, 4 }, new long[] { 1, 8 }));
        Assert.Null(err);
        Assert.Contains("l.company_id = any(@scopeCompanies)", b!.Where);
        Assert.Contains("l.site_id = any(@scopeSites)", b.Where);
        Assert.Equal(new long[] { 1, 4 }, b.Params.Get<long[]>("scopeSites"));
        Assert.Equal(new long[] { 1, 8 }, b.Params.Get<long[]>("scopeCompanies"));
    }

    [Fact]
    public void Super_admin_rows_are_not_bound()
    {
        var (b, _) = Reports.Build("bc30-export", Req(), SuperAdmin());
        Assert.DoesNotContain("scopeSites", b!.Where);
        Assert.DoesNotContain("scopeCompanies", b.Where);
    }

    [Fact]
    public void Site_filter_narrows_within_scope_and_refuses_outside_it()
    {
        var user = SiteUser(new long[] { 1, 4 });
        var (b, err) = Reports.Build("bc30-export", Req(filters: new() { ["siteId"] = "4" }), user);
        Assert.Null(err);
        Assert.Contains("l.site_id = @site", b!.Where);

        var (_, err2) = Reports.Build("bc30-export", Req(filters: new() { ["siteId"] = "2" }), user);
        Assert.Equal(403, Status(err2));
    }

    [Fact]
    public void Opt_in_column_is_refused_until_the_site_enables_it()
    {
        var user = SiteUser(new long[] { 1 });
        var starter = View(new long[] { 1 });
        Assert.Equal(403, Status(Reports.Build("bc27-out", Req(new[] { Asal }), user, starter).error));
        Assert.Equal(403, Status(Reports.Build("bc27-out", Req(sort: new[] { new SortSpec(Asal, "asc") }), user, starter).error));

        var enabled = View(new long[] { 1 }, columns: new() { [(1, "bc27-out")] = new[] { "Kode Barang", Asal } });
        Assert.Null(Reports.Build("bc27-out", Req(new[] { Asal }), user, enabled).error);
    }

    [Fact]
    public void Hidden_column_is_refused_on_export_shaped_requests_too()
    {
        var v = View(new long[] { 1 }, columns: new() { [(1, "bc27-out")] = new[] { "Kode Barang" } });
        var (_, err) = Reports.Build("bc27-out", Req(new[] { "Insurance" }), SiteUser(new long[] { 1 }), v);
        Assert.Equal(403, Status(err));
    }

    [Fact]
    public void Unknown_column_is_still_a_400()
    {
        var (_, err) = Reports.Build("bc27-out", Req(new[] { "Nope" }), SuperAdmin());
        Assert.Equal(400, Status(err));
    }

    [Fact]
    public void Search_only_covers_visible_columns()
    {
        var user = SiteUser(new long[] { 1 });
        var filters = new Dictionary<string, string?> { ["search"] = "000123" };

        var starter = Reports.Build("bc27-out", Req(filters: filters), user, View(new long[] { 1 })).built!;
        Assert.DoesNotContain(Asal, starter.Where);

        var enabled = View(new long[] { 1 }, columns: new() { [(1, "bc27-out")] = new[] { "Kode Barang", Asal } });
        Assert.Contains(Asal, Reports.Build("bc27-out", Req(filters: filters), user, enabled).built!.Where);

        // no searchable column visible at all -> the search matches nothing rather than building "()"
        var none = View(new long[] { 1 }, columns: new() { [(1, "bc27-out")] = new[] { "Insurance" } });
        Assert.Contains("and (false)", Reports.Build("bc27-out", Req(filters: filters), user, none).built!.Where);
    }

    [Fact]
    public void Defaults_follow_the_site_columns()
    {
        var r = Report("bc27-out");
        Assert.Equal(r.Defaults, Reports.DefaultsFor(r, null));

        var withAsal = SiteReports.Starter(r).Append(Asal).ToHashSet();
        var d = Reports.DefaultsFor(r, withAsal);
        Assert.Equal(r.Defaults, d.Take(r.Defaults.Length));
        Assert.Equal(Asal, d.Last());                      // an enabled opt-in column opens visible

        var noDefaults = new HashSet<string> { "Bukti Pengeluaran / Nomor", "Bukti Pengeluaran / Tanggal" };
        Assert.Equal(noDefaults, Reports.DefaultsFor(r, noDefaults).ToHashSet());
    }

    [Fact]
    public void Default_columns_request_respects_hidden_defaults()
    {
        var v = View(new long[] { 1 }, columns: new() { [(1, "bc27-out")] = new[] { "Kode Barang", "Nama Barang" } });
        var (b, err) = Reports.Build("bc27-out", Req(), SiteUser(new long[] { 1 }), v);
        Assert.Null(err);
        Assert.Equal(new[] { "Kode Barang", "Nama Barang" }, b!.Columns);
    }
}
