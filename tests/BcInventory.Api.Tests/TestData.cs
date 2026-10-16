using Microsoft.AspNetCore.Http;

namespace BcInventory.Api.Tests;

/// <summary>Scopes, site snapshots and helpers shared by the tests.</summary>
internal static class TestData
{
    public static UserScope SuperAdmin() =>
        new(1, "admin@x", "Admin", Auth.SuperAdmin, AllSites: true, SiteIds: Array.Empty<long>(),
            AllCompanies: true, CompanyIds: Array.Empty<long>());

    public static UserScope SiteUser(long[] sites, long[]? companies = null) =>
        new(2, "site@x", "Site", "Site BC User", AllSites: false, SiteIds: sites,
            AllCompanies: false, CompanyIds: companies ?? new long[] { 1 });

    /// <summary>A site-visibility view over an in-memory configuration.</summary>
    public static SiteReports.View View(long[] sites,
        Dictionary<long, string[]>? reports = null,
        Dictionary<(long, string), string[]>? columns = null) =>
        new(sites, new SiteReports.Snapshot(
            (reports ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet()),
            (columns ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet())));

    public static Catalog.Report Report(string key) => Catalog.Get(key)!;

    public static int? Status(IResult? r) => (r as IStatusCodeHttpResult)?.StatusCode;
}
