using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <summary>
/// Site scoping. A site belongs to one company; a user is linked to any number of companies and
/// any number of their sites, or to all sites of their companies. Data rows carry the site they
/// were uploaded for, and a user sees a row only when both its company and its site are theirs.
/// </summary>
public static class Sites
{
    /// <summary>
    /// SQL predicate bounding rows (table alias <paramref name="alias"/>) to the user's companies
    /// and sites, starting with " and ". Binds @scopeCompanies and @scopeSites — add both with
    /// <see cref="Params"/>. Empty lists match nothing, which is the safe default.
    /// </summary>
    public static string RowFilter(UserScope s, string alias = "l") =>
        (s.AllCompanies ? "" : $" and {alias}.company_id = any(@scopeCompanies)")
        + (s.AllSites ? "" : $" and {alias}.site_id = any(@scopeSites)");

    public static void Params(DynamicParameters p, UserScope s)
    {
        p.Add("scopeCompanies", s.CompanyIds);
        p.Add("scopeSites", s.SiteIds);
    }

    /// <summary>The account's current scope, read from the database.</summary>
    public static async Task<UserScope> ScopeOf(NpgsqlConnection con, dynamic user)
    {
        var id = (long)user.id;
        var role = (string)user.role;
        var companyIds = (await con.QueryAsync<long>(
            "select company_id from auth.user_companies where user_id = @id", new { id })).ToArray();
        var siteIds = (await con.QueryAsync<long>(
            "select site_id from auth.user_sites where user_id = @id", new { id })).ToArray();
        // auth.users.all_entities keeps its historic name; it now means "all sites of my companies".
        return new UserScope(id, (string)user.email, (string)user.full_name, role,
                             role == Auth.SuperAdmin || (bool)user.all_entities, siteIds,
                             role == Auth.SuperAdmin, companyIds);
    }

    /// <summary>Sites the user may upload for or filter by, with their company.</summary>
    public static async Task<List<SiteRow>> Mine(NpgsqlConnection con, UserScope scope) =>
        (await con.QueryAsync<SiteRow>($"""
            select s.id, s.name, s.company_id as companyid
            from master.sites s join master.companies c on c.id = s.company_id
            where c.active
              {(scope.AllCompanies ? "" : "and s.company_id = any(@scopeCompanies)")}
              {(scope.AllSites ? "" : "and s.id = any(@scopeSites)")}
            order by s.name
            """, new { scopeCompanies = scope.CompanyIds, scopeSites = scope.SiteIds })).ToList();

    public record SiteRow(long Id, string Name, long CompanyId);

    /// <summary>
    /// Resolve and validate the site an upload's rows are tagged with. A user with exactly one
    /// site in the upload's company need not choose; anyone else must.
    /// </summary>
    public static async Task<(long? siteId, IResult? err)> ResolveUpload(NpgsqlConnection con, UserScope scope, long companyId, long? requested)
    {
        var mine = (await Mine(con, scope)).Where(s => s.CompanyId == companyId).ToList();
        if (requested is null && mine.Count == 1) return (mine[0].Id, null);
        if (requested is null)
            return (null, Results.Problem(statusCode: 400, title: "VAL-001",
                detail: mine.Count == 0 ? "This company has no site you may upload for. An administrator can add one under Master Data."
                                        : "Select the site this data belongs to."));
        if (mine.All(s => s.Id != requested))
            return (null, Results.Problem(statusCode: 403, title: "SCOPE-002", detail: "Requested site is outside your scope or not in the chosen company."));
        return (requested, null);
    }
}
