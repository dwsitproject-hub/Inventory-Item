using Dapper;
using Npgsql;

namespace BcInventory.Api;

public static class Db
{
    /// <summary>
    /// Applies the schema, or verifies it is already there.
    ///
    /// Creating and altering tables needs rights the running application should not hold: a
    /// least-privilege account cannot ALTER a table it does not own, and cannot CREATE SCHEMA
    /// without rights over the whole database. Granting those back would hand the application
    /// the ability to drop its own audit trigger, which is the point of restricting it (AR-06).
    ///
    /// So migrating and running are separate jobs. With autoMigrate off the application only
    /// checks the schema is present and refuses to start if it is not, rather than failing
    /// obscurely on the first query. Apply schema changes with: dotnet BcInventory.Api.dll --migrate
    /// using an account that owns the tables.
    /// </summary>
    public static async Task EnsureCreated(NpgsqlDataSource ds, bool autoMigrate = true)
    {
        if (!autoMigrate)
        {
            await using var check = await ds.OpenConnectionAsync();
            var ready = await check.ExecuteScalarAsync<bool>(
                "select to_regclass('auth.users') is not null and to_regclass('audit.audit_events') is not null");
            if (!ready)
                throw new InvalidOperationException(
                    "The database schema is missing and this account is not allowed to create it. " +
                    "Run the API once with --migrate using an account that owns the tables, then start normally.");
            var hasCol = await check.ExecuteScalarAsync<bool>(
                "select exists (select 1 from information_schema.columns " +
                "where table_schema='auth' and table_name='users' and column_name='tokens_valid_from')");
            if (!hasCol)
                throw new InvalidOperationException(
                    "The database schema is behind this build (auth.users.tokens_valid_from is missing). " +
                    "Run the API once with --migrate using an account that owns the tables.");
            Console.WriteLine("[schema] verified (auto-migrate off — this account may not alter the schema)");
            return;
        }

        await using var con = await ds.OpenConnectionAsync();
        await con.ExecuteAsync("""
            create schema if not exists master;
            create schema if not exists auth;
            create schema if not exists ingest;
            create schema if not exists bc;

            create table if not exists master.entities (
                id bigint generated always as identity primary key,
                code text not null unique,
                name text not null unique
            );
            create table if not exists master.sites (
                id bigint generated always as identity primary key,
                entity_id bigint not null references master.entities(id),
                name text not null,
                unique (entity_id, name)
            );
            create table if not exists master.tpb_permits (
                id bigint generated always as identity primary key,
                entity_id bigint not null references master.entities(id),
                site_id bigint references master.sites(id),
                permit_no text not null unique
            );

            create table if not exists auth.users (
                id bigint generated always as identity primary key,
                email text not null unique,
                full_name text not null,
                role text not null,
                password_hash text not null,
                all_entities boolean not null default false,
                entity_id bigint references master.entities(id),
                site_id bigint references master.sites(id),
                status text not null default 'active',
                created_at timestamptz not null default now()
            );

            -- configurable role → page → action matrix (Role Management)
            create table if not exists auth.role_permissions (
                id bigint generated always as identity primary key,
                role text not null,
                page text not null,
                can_view boolean not null default false,
                can_insert boolean not null default false,
                can_edit boolean not null default false,
                updated_at timestamptz not null default now(),
                unique (role, page)
            );
            -- Permanently delete ingested report rows (FR-R14). Default false: only Super Admin
            -- has it until an administrator grants it in Role Management.
            alter table auth.role_permissions add column if not exists can_delete boolean not null default false;

            create table if not exists ingest.ingestion_files (
                id bigint generated always as identity primary key,
                file_name text not null,
                file_hash text not null unique,
                file_size bigint not null,
                source text not null,               -- sap | manual | sample
                template text not null,             -- BC23 | BC40
                status text not null,               -- loaded | partial | failed
                rows_total int not null default 0,
                rows_loaded int not null default 0,
                rows_quarantined int not null default 0,
                header_meta jsonb,
                footer_totals jsonb,
                error text,
                uploaded_by text,
                received_at timestamptz not null default now()
            );
            create table if not exists ingest.quarantine_rows (
                id bigint generated always as identity primary key,
                ingestion_file_id bigint not null references ingest.ingestion_files(id),
                row_no int not null,
                raw_data jsonb not null,
                reasons text[] not null
            );

            create table if not exists bc.documents (
                id bigint generated always as identity primary key,
                template text not null,
                doc_type text,
                aju_number text not null default '',
                doc_number text not null default '',
                doc_date date,
                entity_id bigint not null references master.entities(id),
                site_id bigint references master.sites(id),
                tpb_id bigint references master.tpb_permits(id),
                supplier_name text,
                ingestion_file_id bigint not null references ingest.ingestion_files(id),
                ingested_at timestamptz not null default now(),
                unique (template, aju_number, doc_number)
            );
            create table if not exists bc.document_lines (
                id bigint generated always as identity primary key,
                document_id bigint not null references bc.documents(id),
                template text not null,
                doc_type text,
                doc_date date,
                entity_id bigint not null references master.entities(id),
                site_id bigint references master.sites(id),
                tpb_id bigint references master.tpb_permits(id),
                line_no int not null,
                data jsonb not null,
                ingestion_file_id bigint not null references ingest.ingestion_files(id),
                ingested_at timestamptz not null default now(),
                unique (document_id, line_no)
            );
            create index if not exists ix_lines_scope_date
                on bc.document_lines (template, entity_id, doc_date desc, id);

            create schema if not exists app;
            create table if not exists app.saved_views (
                id bigint generated always as identity primary key,
                user_id bigint not null references auth.users(id),
                report_key text not null,
                name text,                          -- null = implicit "last layout" (FR-R12)
                columns jsonb not null,
                sorts jsonb not null default '[]',
                page_size int not null default 25,
                updated_at timestamptz not null default now()
            );
            create unique index if not exists ux_views_last
                on app.saved_views (user_id, report_key) where name is null;
            create unique index if not exists ux_views_named
                on app.saved_views (user_id, report_key, name) where name is not null;

            create table if not exists app.notifications (
                id bigint generated always as identity primary key,
                event_type text not null,           -- upload | error | quarantine
                title text not null,
                body text,
                created_at timestamptz not null default now()
            );
            create table if not exists app.notification_deliveries (
                id bigint generated always as identity primary key,
                notification_id bigint not null references app.notifications(id),
                user_id bigint not null references auth.users(id),
                read_at timestamptz,
                unique (notification_id, user_id)
            );
            -- Bumped whenever an administrator disables, re-enables or resets an account, which
            -- retires every token issued before that moment (AR-01).
            alter table auth.users add column if not exists tokens_valid_from timestamptz not null default now();

            alter table app.notification_deliveries add column if not exists email_status text;
            alter table app.notification_deliveries add column if not exists email_error text;
            -- A recipient may want a notification by e-mail but not in the bell. The row is still
            -- written so its delivery status has somewhere to live; this flag decides whether the
            -- in-app list shows it.
            alter table app.notification_deliveries add column if not exists in_app boolean not null default true;

            -- Per-user notification routing, managed from Administration -> Notifications.
            -- A user with no row for an event falls back to the event's default roles, so new
            -- users and newly added event types are never silently unsubscribed.
            create table if not exists app.notification_subscriptions (
                id bigint generated always as identity primary key,
                user_id bigint not null references auth.users(id),
                event_type text not null,
                in_app boolean not null default true,
                email boolean not null default true,
                updated_at timestamptz not null default now(),
                unique (user_id, event_type)
            );

            -- immutable audit trail (FR-A7, TechDoc §5.2 audit.audit_events)
            create schema if not exists audit;
            create table if not exists audit.audit_events (
                id bigint generated always as identity primary key,
                occurred_at timestamptz not null default now(),
                actor_id bigint,
                actor_email text,
                actor_role text,
                action text not null,
                target_type text,
                target_id text,
                summary text,
                detail jsonb,
                ip text,
                actor_entity_id bigint
            );
            create index if not exists ix_audit_time on audit.audit_events (occurred_at desc);
            create index if not exists ix_audit_actor on audit.audit_events (actor_email, occurred_at desc);
            create index if not exists ix_audit_action on audit.audit_events (action, occurred_at desc);

            -- append-only: the trail must survive an attacker or a careless admin
            create or replace function audit.no_mutation() returns trigger language plpgsql as $fn$
            begin
                raise exception 'audit.audit_events is append-only (FR-A7)';
            end $fn$;
            drop trigger if exists trg_audit_no_mutation on audit.audit_events;
            create trigger trg_audit_no_mutation
                before update or delete on audit.audit_events
                for each row execute function audit.no_mutation();

            -- A row trigger never sees TRUNCATE, so the append-only guarantee had a way around
            -- it: one statement emptied the whole trail without tripping the guard (AR-08).
            drop trigger if exists trg_audit_no_truncate on audit.audit_events;
            create trigger trg_audit_no_truncate
                before truncate on audit.audit_events
                for each statement execute function audit.no_mutation();
            """);
    }

    /// <summary>
    /// First-run seed. Initial passwords come from configuration so a networked deployment
    /// never ships with the documented local-dev credentials; the seed only runs on an
    /// empty user table, so it can never overwrite real accounts.
    /// </summary>
    private static readonly string[] Placeholders =
        { "CHANGE_ME", "CHANGEME", "PASSWORD", "SECRET", "YOUR_PASSWORD", "XXXX", "TODO" };

    /// <summary>
    /// Refuses a placeholder or too-short seed password. Without this the container starts
    /// happily and creates the admin with a literal value like "CHANGE_ME" — which then cannot
    /// be corrected by editing .env, because seeding never runs again.
    /// </summary>
    private static void ValidateSeedPassword(string? pw, string envName)
    {
        if (string.IsNullOrWhiteSpace(pw)) return;            // unset → local-dev default
        if (Placeholders.Contains(pw.Trim().ToUpperInvariant()) || pw.Trim().Length < 8)
            throw new InvalidOperationException(
                $"{envName} is still a placeholder or shorter than 8 characters. " +
                "Set a real value in the tier's .env before first start — the seed runs only once, " +
                "on an empty user table, so a bad value here cannot be fixed by editing .env later.");
    }

    public static async Task Seed(NpgsqlDataSource ds, IConfiguration? cfg = null)
    {
        await using var con = await ds.OpenConnectionAsync();

        var adminPw = cfg?["Seed:AdminPassword"];
        var sitePw = cfg?["Seed:SitePassword"];
        ValidateSeedPassword(adminPw, "Seed__AdminPassword");
        ValidateSeedPassword(sitePw, "Seed__SitePassword");

        var haveUsers = await con.ExecuteScalarAsync<int>("select count(*) from auth.users");
        if (haveUsers > 0)
        {
            // Rescue hatch for a locked-out environment: set Seed__ResetAdminPassword=true,
            // restart once, then REMOVE it. Only ever touches the Super Admin account.
            if (string.Equals(cfg?["Seed:ResetAdminPassword"], "true", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(adminPw))
            {
                var email = await con.ExecuteScalarAsync<string?>("""
                    update auth.users set password_hash = @h, status = 'active'
                    where id = (select id from auth.users where role = 'Super Admin' order by id limit 1)
                    returning email
                    """, new { h = BCrypt.Net.BCrypt.HashPassword(adminPw, 11) });
                Console.WriteLine(email is null
                    ? "[seed] ResetAdminPassword requested but no Super Admin account exists"
                    : $"[seed] SUPER ADMIN PASSWORD RESET for {email} — remove Seed__ResetAdminPassword from .env now");
                if (email is not null)
                    Audit.Log("admin.user.reset", null, "user", email,
                        "Super Admin password reset via Seed__ResetAdminPassword at startup",
                        null, actorEmailOverride: "system@startup");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(adminPw)) adminPw = "Admin123!";
        if (string.IsNullOrWhiteSpace(sitePw)) sitePw = "Bontang123!";

        var entityId = await con.ExecuteScalarAsync<long>("""
            insert into master.entities (code, name) values ('EUP', 'PT Energi Unggul Persada')
            on conflict (code) do update set name = excluded.name
            returning id
            """);
        var siteId = await con.ExecuteScalarAsync<long>(
            "insert into master.sites (entity_id, name) values (@e, 'BONTANG') on conflict do nothing returning id",
            new { e = entityId });

        // password hashing: BCrypt work factor 11 — local testing accounts
        await con.ExecuteAsync("""
            insert into auth.users (email, full_name, role, password_hash, all_entities, entity_id, site_id)
            values (@a, 'Jerry Pratama', 'Super Admin', @ph1, true, null, null),
                   (@b, 'Aziz Wijonarko', 'Site BC User', @ph2, false, @e, @s)
            """,
            new
            {
                a = "admin@energi-up.com",
                b = "bc.bontang@energi-up.com",
                ph1 = BCrypt.Net.BCrypt.HashPassword(adminPw, 11),
                ph2 = BCrypt.Net.BCrypt.HashPassword(sitePw, 11),
                e = entityId,
                s = siteId
            });
        var custom = cfg?["Seed:AdminPassword"] is { Length: > 0 };
        Console.WriteLine("[seed] master data + 2 users created (admin@energi-up.com, bc.bontang@energi-up.com) — "
            + (custom ? "passwords from Seed__AdminPassword / Seed__SitePassword"
                      : "DEFAULT local-dev passwords; set Seed__AdminPassword before any networked deployment"));
    }
}
