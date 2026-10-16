using Xunit;
using static BcInventory.Api.Tests.TestData;

namespace BcInventory.Api.Tests;

/// <summary>Which reports and columns a user's sites may see (Admin → Master Data → site → Reports).</summary>
public class SiteReportsTests
{
    private const string Asal = "Dokumen Asal Barang / No Daftar";

    [Fact]
    public void All_sites_user_sees_every_report_and_every_column()
    {
        var v = View(Array.Empty<long>(), new() { [1] = new[] { "bc30-export" } });
        Assert.All(Catalog.Reports, r => Assert.True(v.Sees(r)));
        Assert.Null(v.Columns(Report("bc27-out")));
    }

    [Fact]
    public void Unconfigured_site_sees_every_report_with_starter_columns()
    {
        var v = View(new long[] { 1 });
        Assert.All(Catalog.Reports, r => Assert.True(v.Sees(r)));
        var cols = v.Columns(Report("bc27-out"))!;
        Assert.Equal(SiteReports.Starter(Report("bc27-out")).ToHashSet(), cols);
        Assert.DoesNotContain(Asal, cols);
    }

    [Fact]
    public void Configured_page_is_narrowed_and_the_other_page_stays_whole()
    {
        var v = View(new long[] { 1 }, new() { [1] = new[] { "bc30-export", "bc33-export" } });
        Assert.True(v.Sees(Report("bc30-export")));
        Assert.False(v.Sees(Report("pib-import")));
        Assert.True(v.Sees(Report("wip")));                // nothing ticked on Inventory Movement
    }

    [Fact]
    public void Several_sites_see_the_union_of_their_reports()
    {
        var v = View(new long[] { 1, 2 }, new() { [1] = new[] { "bc30-export" }, [2] = new[] { "pib-import" } });
        Assert.True(v.Sees(Report("bc30-export")));
        Assert.True(v.Sees(Report("pib-import")));
        Assert.False(v.Sees(Report("bc40-receipt")));
    }

    [Fact]
    public void A_site_that_leaves_a_page_open_opens_it_for_the_user()
    {
        // site 2 has nothing configured, so every report is visible through it
        var v = View(new long[] { 1, 2 }, new() { [1] = new[] { "bc30-export" } });
        Assert.True(v.Sees(Report("pib-import")));
    }

    [Fact]
    public void Custom_columns_replace_the_starter_set()
    {
        var r = Report("bc27-out");
        var custom = r.Fields.Select(f => f.Name).Where(n => n != "Insurance").ToArray();
        var v = View(new long[] { 1 }, columns: new() { [(1, "bc27-out")] = custom });
        var cols = v.Columns(r)!;
        Assert.Contains(Asal, cols);
        Assert.DoesNotContain("Insurance", cols);
    }

    [Fact]
    public void Columns_are_the_union_over_sites()
    {
        var r = Report("bc27-out");
        var v = View(new long[] { 1, 2 },
            columns: new() { [(1, "bc27-out")] = new[] { "Kode Barang", Asal } });   // site 2: starter
        var cols = v.Columns(r)!;
        Assert.Contains(Asal, cols);
        Assert.Contains("Insurance", cols);                // from site 2's starter set
    }

    [Fact]
    public void A_site_that_hides_the_report_does_not_add_columns()
    {
        var r = Report("bc27-out");
        var v = View(new long[] { 1, 2 },
            reports: new() { [2] = new[] { "pib-import" } },                     // site 2 hides bc27-out
            columns: new() { [(1, "bc27-out")] = new[] { "Kode Barang" }, [(2, "bc27-out")] = new[] { Asal } });
        Assert.Equal(new HashSet<string> { "Kode Barang" }, v.Columns(r));
    }

    [Fact]
    public void Require_refuses_a_hidden_report_and_ignores_an_unknown_key()
    {
        var v = View(new long[] { 1 }, new() { [1] = new[] { "bc30-export" } });
        Assert.Equal(403, Status(SiteReports.Require(v, "pib-import")));
        Assert.Null(SiteReports.Require(v, "bc30-export"));
        Assert.Null(SiteReports.Require(v, "no-such-report"));   // the handler answers 404
    }
}
