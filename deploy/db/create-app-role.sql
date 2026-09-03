-- BC Inventory — least-privilege database role (AR-06)
--
-- The API currently connects to ApsaraDB as the instance's privileged account. That means an
-- injection or an application compromise inherits full database rights — including the ability
-- to disable the append-only audit trigger, which is the control the customs record rests on.
-- An application should not be able to defeat its own guarantees.
--
-- Run this ONCE as the privileged account, then set DB_USER / DB_PASSWORD in
-- deploy/backend/.env to the new role and recreate the API container.
--
--   psql -h <endpoint> -U <privileged account> -d bcinventory -f create-app-role.sql
--
-- Choose a password before running and substitute it below; do not leave the placeholder.

\set app_password 'CHANGE_ME_BEFORE_RUNNING'

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'bcapp_rw') then
    execute format('create role bcapp_rw login password %L', :'app_password');
  else
    execute format('alter role bcapp_rw password %L', :'app_password');
  end if;
end $$;

-- No superuser, no role creation, no database creation. Stated explicitly so a later
-- "grant it everything to make the error go away" is a visible change rather than a default.
alter role bcapp_rw nosuperuser nocreatedb nocreaterole noreplication nobypassrls;

grant connect on database bcinventory to bcapp_rw;

-- Read and write the application's own data, and nothing else.
do $$
declare s text;
begin
  foreach s in array array['app','audit','auth','bc','ingest','master'] loop
    execute format('grant usage on schema %I to bcapp_rw', s);
    execute format('grant select, insert, update, delete on all tables in schema %I to bcapp_rw', s);
    execute format('grant usage, select on all sequences in schema %I to bcapp_rw', s);
    -- Tables the application creates later (EnsureCreated runs at startup) inherit the same grants.
    execute format('alter default privileges in schema %I grant select, insert, update, delete on tables to bcapp_rw', s);
    execute format('alter default privileges in schema %I grant usage, select on sequences to bcapp_rw', s);
  end loop;
end $$;

-- The audit trail is append-only. Withholding UPDATE and DELETE means the guarantee no longer
-- depends only on the trigger: even with the trigger disabled, this role cannot rewrite history.
revoke update, delete, truncate on audit.audit_events from bcapp_rw;

-- The application creates its own schema at startup, so it needs CREATE on those schemas.
-- If you would rather it could not, run the schema migration as the privileged account and
-- revoke this — the application tolerates the objects already existing.
do $$
declare s text;
begin
  foreach s in array array['app','audit','auth','bc','ingest','master'] loop
    execute format('grant create on schema %I to bcapp_rw', s);
  end loop;
end $$;

-- Nothing in public, and no rights on other databases.
revoke all on schema public from bcapp_rw;

-- Verify: this should list the six schemas and no superuser attribute.
select rolname, rolsuper, rolcreatedb, rolcreaterole from pg_roles where rolname = 'bcapp_rw';
select table_schema, count(*) filter (where privilege_type = 'SELECT') as can_read,
       count(*) filter (where privilege_type = 'DELETE') as can_delete
from information_schema.table_privileges
where grantee = 'bcapp_rw'
group by table_schema order by table_schema;
