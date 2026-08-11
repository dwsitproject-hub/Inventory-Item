using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <summary>
/// LPM saldo accountability + reconciliation (FR-R8): opening + in − out − adj = closing per material per month.
/// Only inbound extracts are loaded so far, so out/adj are 0 — the equation and variance flags are fully wired
/// and outbound documents will slot in when their extracts are specified.
/// </summary>
public static class Lpm
{
    public static async Task<IResult> Saldo(NpgsqlDataSource ds, UserScope scope, string? search, int? limit)
    {
        var scopeAnd = scope.AllEntities ? "" : " and l.entity_id = @scopeEntity";
        var searchAnd = string.IsNullOrWhiteSpace(search) ? "" :
            " and (coalesce(l.data->>'Material Code', l.data->>'Material / Code') ilike @search" +
            " or coalesce(l.data->>'Commodity', l.data->>'Material / Description') ilike @search)";

        var sql = $"""
            with m as (
                select coalesce(l.data->>'Material Code', l.data->>'Material / Code') as material,
                       min(coalesce(l.data->>'Commodity', l.data->>'Material / Description')) as description,
                       min(coalesce(l.data->>'Unit', l.data->>'BC / UOM')) as uom,
                       date_trunc('month', l.doc_date)::date as month,
                       sum(coalesce(
                           nullif(l.data->>'Qty','')::numeric,
                           nullif(l.data->>'BC / Qty','')::numeric, 0)) as qty_in,
                       count(*) as line_count
                from bc.document_lines l
                where l.doc_date is not null
                  and coalesce(l.data->>'Material Code', l.data->>'Material / Code') is not null
                  {scopeAnd}{searchAnd}
                group by 1, 4
            )
            select material, description, uom, month,
                   coalesce(sum(qty_in) over (partition by material order by month
                       rows between unbounded preceding and 1 preceding), 0) as "opening",
                   qty_in as "qtyIn",
                   0::numeric as "qtyOut",
                   0::numeric as "adjustment",
                   sum(qty_in) over (partition by material order by month) as "closing",
                   line_count as "lines"
            from m
            order by material, month
            limit @limit
            """;

        await using var con = await ds.OpenConnectionAsync();
        var rows = (await con.QueryAsync(sql, new
        {
            scopeEntity = scope.EntityId,
            search = "%" + (search ?? "").Trim() + "%",
            limit = Math.Clamp(limit ?? 500, 1, 2000)
        })).ToList();
        return Results.Ok(new { rows, note = "Outbound extracts not yet loaded — Out and Adj are 0; the saldo equation is opening + in − out − adj = closing." });
    }

    /// <summary>BC 4.0 goods-receipt realisation variances: delivered vs declared beyond tolerance.</summary>
    public static async Task<IResult> Variances(NpgsqlDataSource ds, UserScope scope, int? limit)
    {
        var scopeAnd = scope.AllEntities ? "" : " and l.entity_id = @scopeEntity";
        var sql = $"""
            with v as (
                select l.data->>'Location' as location,
                       l.data->>'TPB No.' as "tpbNo",
                       l.data->>'Document BC / No' as "docNo",
                       nullif(l.data->>'Document BC / Date','')::date as "docDate",
                       l.data->>'Vendor Name' as vendor,
                       l.data->>'Material / Code' as material,
                       l.data->>'Material / Description' as description,
                       nullif(l.data->>'BC / Qty','')::numeric as "bcQty",
                       l.data->>'BC / UOM' as uom,
                       nullif(l.data->>'Realization of Good Receipts / Delivery Qty','')::numeric as "deliveryQty",
                       nullif(l.data->>'Realization of Good Receipts / Complete Qty','')::numeric as "completeQty",
                       coalesce(
                           nullif(l.data->>'Realization of Good Receipts / (+/-)','')::numeric,
                           nullif(l.data->>'Realization of Good Receipts / Delivery Qty','')::numeric
                             - nullif(l.data->>'BC / Qty','')::numeric) as variance,
                       nullif(replace(replace(l.data->>'BC / Tolerance','%',''),' ',''),'')::numeric as "tolerancePct"
                from bc.document_lines l
                where l.template = 'BC40'{scopeAnd}
            )
            select *,
                   case when "bcQty" is not null and "bcQty" <> 0 and variance is not null
                        then round(abs(variance) / abs("bcQty") * 100, 2) end as "variancePct",
                   case when variance is not null and variance <> 0
                             and ("tolerancePct" is null
                                  or abs(variance) > abs(coalesce("bcQty",0)) * "tolerancePct" / 100)
                        then true else false end as "beyondTolerance"
            from v
            where variance is not null and variance <> 0
            order by abs(variance) desc
            limit @limit
            """;

        await using var con = await ds.OpenConnectionAsync();
        var rows = (await con.QueryAsync(sql, new { scopeEntity = scope.EntityId, limit = Math.Clamp(limit ?? 100, 1, 500) })).ToList();
        var summary = await con.QueryFirstAsync($"""
            select count(*) filter (where coalesce(
                       nullif(l.data->>'Realization of Good Receipts / (+/-)','')::numeric,
                       nullif(l.data->>'Realization of Good Receipts / Delivery Qty','')::numeric
                         - nullif(l.data->>'BC / Qty','')::numeric) <> 0) as "withVariance",
                   count(*) filter (where l.data ? 'Realization of Good Receipts / Delivery Qty') as "deliveryTracked",
                   count(*) as "totalLines"
            from bc.document_lines l where l.template = 'BC40'{scopeAnd}
            """, new { scopeEntity = scope.EntityId });
        return Results.Ok(new
        {
            rows,
            summary = new
            {
                withVariance = (long)summary.withVariance,
                deliveryTracked = (long)summary.deliveryTracked,
                totalLines = (long)summary.totalLines
            }
        });
    }
}
