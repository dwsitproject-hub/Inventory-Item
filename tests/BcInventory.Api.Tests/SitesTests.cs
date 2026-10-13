using Dapper;
using Xunit;
using static BcInventory.Api.Tests.TestData;

namespace BcInventory.Api.Tests;

/// <summary>The shared row-scope predicate used by Reports, LPM and the Ingestion list.</summary>
public class SitesTests
{
    [Fact]
    public void Scoped_user_is_bound_by_company_and_site()
    {
        Assert.Equal(" and f.company_id = any(@scopeCompanies) and f.site_id = any(@scopeSites)",
            Sites.RowFilter(SiteUser(new long[] { 3 }), "f"));
    }

    [Fact]
    public void All_sites_user_is_bound_by_company_only()
    {
        var allSites = SiteUser(Array.Empty<long>()) with { AllSites = true };
        Assert.Equal(" and l.company_id = any(@scopeCompanies)", Sites.RowFilter(allSites));
    }

    [Fact]
    public void Super_admin_is_not_bound()
    {
        Assert.Equal("", Sites.RowFilter(SuperAdmin()));
    }

    [Fact]
    public void Params_bind_both_lists()
    {
        var p = new DynamicParameters();
        Sites.Params(p, SiteUser(new long[] { 3, 5 }, new long[] { 2 }));
        Assert.Equal(new long[] { 3, 5 }, p.Get<long[]>("scopeSites"));
        Assert.Equal(new long[] { 2 }, p.Get<long[]>("scopeCompanies"));
    }
}
