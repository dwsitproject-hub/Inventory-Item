using System.Text;
using BcInventory.Api;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Upload ceiling enforced by the handler, with a friendly message (see /ingestions/upload).
const long MaxUploadBytes = 100L * 1024 * 1024;
// Transport limits sit deliberately ABOVE it: Kestrel defaults to 30,000,000 bytes, which is
// smaller than a real BC 4.0 export (31.3 MB) and would reject it with an opaque 413 before
// the handler ever sees the file.
const long TransportLimitBytes = 120L * 1024 * 1024;

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = TransportLimitBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = TransportLimitBytes;
    o.MultipartHeadersLengthLimit = 32 * 1024;
});

DapperTypes.Register();              // DateOnly/TimeOnly parameters (report date filters)

var ds = NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Db")!);
builder.Services.AddSingleton(ds);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "bc-inventory",
            ValidAudience = "bc-inventory",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        o.MapInboundClaims = false;
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ---------- bootstrap: schema, seed, auto-ingest samples ----------
await Db.EnsureCreated(ds);
Audit.Configure(ds);                 // before Seed, so a startup password reset is audited
Notifications.Configure(ds, app.Configuration);
await Db.Seed(ds, app.Configuration);
Permissions.Configure(ds);
await Permissions.EnsureSeeded(ds);
_ = Task.Run(async () =>
{
    try { await Ingestion.AutoIngestSamples(ds, app.Configuration["SampleData:Dir"] ?? "/samples"); }
    catch (Exception ex) { Console.WriteLine("[samples] failed: " + ex); }
});

var api = app.MapGroup("/api/v1");

api.MapGet("/health", async () =>
{
    await using var con = await ds.OpenConnectionAsync();
    var ok = await con.ExecuteScalarAsync<int>("select 1");
    return Results.Ok(new { status = "ok", db = ok == 1 });
});

api.MapPost("/auth/login", (LoginRequest req, HttpContext ctx) =>
    Auth.Login(ds, app.Configuration, req, ctx.Connection.RemoteIpAddress?.ToString()));

api.MapGet("/me", (System.Security.Claims.ClaimsPrincipal user) => Auth.Me(ds, user)).RequireAuthorization();

api.MapGet("/reports", () => Results.Ok(Reports.CatalogList())).RequireAuthorization();

api.MapPost("/reports/{key}/query", async (string key, QueryRequest req, HttpContext ctx) =>
{
    var scope = Auth.Scope(ctx.User);
    var page = Catalog.Get(key)?.Page ?? "reports";
    if (await Permissions.Require(scope, page, "view") is { } err) return err;
    return await Reports.Query(ds, key, req, scope, ctx.Connection.RemoteIpAddress?.ToString());
}).RequireAuthorization();

api.MapPost("/reports/{key}/export", async (string key, string? format, QueryRequest req, HttpContext ctx) =>
{
    var scope = Auth.Scope(ctx.User);
    var page = Catalog.Get(key)?.Page ?? "reports";
    if (await Permissions.Require(scope, page, "edit") is { } err) return err;
    return await Exports.Run(ds, key, format ?? "xlsx", req, scope, ctx.Connection.RemoteIpAddress?.ToString());
}).RequireAuthorization();

api.MapGet("/reports/{key}/template", (string key, HttpContext ctx) =>
{
    var report = Catalog.Get(key);
    if (report is null)
        return Results.Problem(statusCode: 404, title: "RPT-001", detail: $"Unknown report key '{key}'.");
    var (bytes, name) = TemplateFiles.Build(report);
    Audit.Log("template.download", Auth.Scope(ctx.User), "report", key,
        $"Downloaded blank upload template for {report.Title}", null,
        ctx.Connection.RemoteIpAddress?.ToString());
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
}).RequireAuthorization();

// ---------- role management (configurable page permissions) ----------
api.MapGet("/admin/role-permissions", (System.Security.Claims.ClaimsPrincipal user) =>
    Permissions.List(ds, Auth.Scope(user))).RequireAuthorization();

api.MapPut("/admin/role-permissions", (RolePermissionUpdate req, HttpContext ctx) =>
    Permissions.Update(ds, Auth.Scope(ctx.User), req)).RequireAuthorization();

// ---------- audit trail (FR-A7) ----------
api.MapGet("/audit", (string? from, string? to, string? actor, string? action, string? search,
                      int? limit, int? offset, HttpContext ctx) =>
    Audit.Query(ds, Auth.Scope(ctx.User), from, to, actor, action, search, limit, offset)).RequireAuthorization();

api.MapGet("/audit/export", (string? from, string? to, string? actor, string? action, string? search, HttpContext ctx) =>
    Audit.Export(ds, Auth.Scope(ctx.User), from, to, actor, action, search)).RequireAuthorization();

api.MapGet("/reports/{key}/views", (string key, HttpContext ctx) =>
    SavedViews.List(ds, Auth.Scope(ctx.User), key)).RequireAuthorization();

api.MapPut("/reports/{key}/views", (string key, ViewPayload payload, HttpContext ctx) =>
    SavedViews.Save(ds, Auth.Scope(ctx.User), key, payload)).RequireAuthorization();

api.MapDelete("/views/{id:long}", (long id, HttpContext ctx) =>
    SavedViews.Delete(ds, Auth.Scope(ctx.User), id)).RequireAuthorization();

api.MapGet("/notifications", (System.Security.Claims.ClaimsPrincipal user) =>
    Notifications.List(ds, Auth.Scope(user))).RequireAuthorization();

api.MapPost("/notifications/{id:long}/read", (long id, HttpContext ctx) =>
    Notifications.MarkRead(ds, Auth.Scope(ctx.User), id)).RequireAuthorization();

api.MapPost("/notifications/read-all", (System.Security.Claims.ClaimsPrincipal user) =>
    Notifications.MarkAllRead(ds, Auth.Scope(user))).RequireAuthorization();

api.MapGet("/dashboard/summary", async (System.Security.Claims.ClaimsPrincipal user) =>
{
    var scope = Auth.Scope(user);
    if (await Permissions.Require(scope, "dashboard", "view") is { } err) return err;
    return await Dashboard.Summary(ds, scope);
}).RequireAuthorization();

// ---------- LPM / reconciliation (FR-R8) ----------
api.MapGet("/lpm/saldo", async (string? search, int? limit, System.Security.Claims.ClaimsPrincipal user) =>
{
    var scope = Auth.Scope(user);
    if (await Permissions.Require(scope, "lpm", "view") is { } err) return err;
    return await Lpm.Saldo(ds, scope, search, limit);
}).RequireAuthorization();

api.MapGet("/lpm/variances", async (int? limit, System.Security.Claims.ClaimsPrincipal user) =>
{
    var scope = Auth.Scope(user);
    if (await Permissions.Require(scope, "lpm", "view") is { } err) return err;
    return await Lpm.Variances(ds, scope, limit);
}).RequireAuthorization();

// ---------- administration (FR-A1/A3/A4) ----------
api.MapGet("/admin/users", (System.Security.Claims.ClaimsPrincipal user) =>
    Admin.ListUsers(ds, Auth.Scope(user))).RequireAuthorization();

api.MapPost("/admin/users", (CreateUserRequest req, HttpContext ctx) =>
    Admin.CreateUser(ds, Auth.Scope(ctx.User), req)).RequireAuthorization();

api.MapPost("/admin/users/{id:long}/status", (long id, StatusRequest req, HttpContext ctx) =>
    Admin.SetStatus(ds, Auth.Scope(ctx.User), id, req)).RequireAuthorization();

api.MapPost("/admin/users/{id:long}/reset", (long id, ResetPasswordRequest req, HttpContext ctx) =>
    Admin.ResetPassword(ds, Auth.Scope(ctx.User), id, req)).RequireAuthorization();

api.MapGet("/admin/master", (System.Security.Claims.ClaimsPrincipal user) =>
    Admin.Master(ds, Auth.Scope(user))).RequireAuthorization();

// ---------- notification routing: who receives which alert, on which channel ----------
api.MapGet("/admin/notification-subscriptions", (System.Security.Claims.ClaimsPrincipal user) =>
    Notifications.Subscriptions(ds, Auth.Scope(user))).RequireAuthorization();

api.MapPut("/admin/notification-subscriptions", (NotificationSubscriptionUpdate req, HttpContext ctx) =>
    Notifications.UpdateSubscriptions(ds, Auth.Scope(ctx.User), req)).RequireAuthorization();

api.MapPost("/admin/entities", (EntityRequest req, HttpContext ctx) =>
    Admin.AddEntity(ds, Auth.Scope(ctx.User), req)).RequireAuthorization();

api.MapPost("/admin/sites", (SiteRequest req, HttpContext ctx) =>
    Admin.AddSite(ds, Auth.Scope(ctx.User), req)).RequireAuthorization();

api.MapPost("/admin/tpb-permits", (PermitRequest req, HttpContext ctx) =>
    Admin.AddPermit(ds, Auth.Scope(ctx.User), req)).RequireAuthorization();

api.MapGet("/ingestions", async (System.Security.Claims.ClaimsPrincipal user) =>
{
    if (await Permissions.Require(Auth.Scope(user), "ingestion", "view") is { } perr) return perr;
    await using var con = await ds.OpenConnectionAsync();
    var rows = await con.QueryAsync("""
        select id, file_name as "fileName", template, source, status,
               rows_total as "rowsTotal", rows_loaded as "rowsLoaded", rows_quarantined as "rowsQuarantined",
               header_meta as "headerMeta", error, uploaded_by as "uploadedBy", received_at as "receivedAt"
        from ingest.ingestion_files order by received_at desc limit 50
        """);
    return Results.Ok(rows);
}).RequireAuthorization();

api.MapGet("/ingestions/{id:long}/quarantine", async (long id, System.Security.Claims.ClaimsPrincipal user) =>
{
    if (await Permissions.Require(Auth.Scope(user), "ingestion", "view") is { } perr) return perr;
    await using var con = await ds.OpenConnectionAsync();
    var rows = await con.QueryAsync("""
        select row_no as "rowNo", raw_data as "rawData", reasons
        from ingest.quarantine_rows where ingestion_file_id = @id order by row_no limit 200
        """, new { id });
    return Results.Ok(rows);
}).RequireAuthorization();

api.MapPost("/ingestions/upload", async (HttpRequest http, HttpContext ctx) =>
{
    var scope = Auth.Scope(ctx.User);
    if (await Permissions.Require(scope, "ingestion", "insert") is { } perr) return perr;
    if (!http.HasFormContentType) return Results.Problem(statusCode: 400, title: "VAL-001", detail: "multipart/form-data expected.");
    var form = await http.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "VAL-001", detail: "file is required.");
    if (file.Length > MaxUploadBytes)
        return Results.Problem(statusCode: 400, title: "VAL-001",
            detail: $"File is {Fmt.N(file.Length / 1024.0 / 1024.0, 1)} MB; the cap is {Fmt.N(MaxUploadBytes / 1024 / 1024)} MB.");

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var result = await Ingestion.Load(ds, file.FileName, ms.ToArray(), "manual", scope.Email);
    return Results.Ok(result);
}).RequireAuthorization();

app.Run();
