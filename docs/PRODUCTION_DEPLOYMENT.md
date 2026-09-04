# Production deployment runbook

Deploying BC Inventory to production. This is not staging: the security items that were an
accepted risk on the closed staging network are **mandatory** here, and every host is shared with
other applications, so **every port must be checked free before it is bound**.

## Production topology

| Tier | Host | Notes |
|---|---|---|
| Frontend | **172.28.80.50** | nginx serving the SPA + the host reverse proxy |
| Backend (API) | **172.28.80.51** | .NET 8 API container; reachable from the frontend host only |
| Database | **pgm-d9jn3khh0b3907w4.pgsql.ap-southeast-5.rds.aliyuncs.com** | ApsaraDB RDS for PostgreSQL (production instance) |

Deployed as containers from the GitHub repo, one tier per host. All commands run over PuTTY/SSH.

---

## What "production" changes versus staging

Do not copy the staging configuration. These five things are different and non-negotiable:

1. **TLS is required.** Credentials and the session token must not cross the network in cleartext.
   Obtain a certificate for the production hostname and enable the HTTPS server block.
2. **The application connects to the database as the least-privilege role** (`bcapp_rw`), never as
   `postgres`. That role cannot alter the schema or delete the audit trail.
3. **`DB_AUTO_MIGRATE=false`.** The app may not change the schema in production; migrations are
   applied deliberately by an owner account.
4. **The frontend is a production build.** It hides the sign-in credential hint and serves the
   security headers. A production build is what `update.sh frontend` produces — never copy a
   dev build across.
5. **Every secret is strong and unique to production.** New `JWT_KEY`, new database password,
   new seed passwords. Nothing shared with staging, nothing left at a default.

---

## 0. Before you start — gather and decide

Collect these and write them into a secure note (not into any tracked file):

| Item | Value to decide/obtain |
|---|---|
| ApsaraDB **privileged** account + password | For creating the role and running migrations (used once, not by the app) |
| New `bcapp_rw` password | You invent it — letters/digits only (avoid `$ # space '`) |
| New `JWT_KEY` | `openssl rand -base64 48` |
| New seed admin / site passwords | ≥10 chars, meet the policy |
| Production hostname | e.g. `it-inventory.kpndomain.com` |
| TLS certificate + key | For that hostname |
| SMTP relay (optional) | Host, port, whether it needs TLS/credentials |
| **DWS Hub SSO** — production issuer | The **production** Hub base URL, e.g. `https://dwshub.kpndomain.com`. Must exactly match the `issuer` in that Hub's discovery document — confirm it with the DWS Hub team (staging was `http://test-dwshub.kpndomain.com`). |
| **DWS Hub SSO** — production client id | A **new** `oauth_client_id` the Hub admin assigns for production (staging used `it-inventory-test`; production gets its own). |

**Whitelist the hosts on the ApsaraDB instance** (Alibaba console → Data Security → Whitelist):
add **172.28.80.50** and **172.28.80.51**. Without this the connection is refused.

**Register the production app in DWS Hub** (Hub Admin → the app). Do this before step 3.4 so SSO
has something to talk to:

- `sso_mode` = `OIDC (strict)`
- `oauth_client_id` = the new production client id (matches `SSO_CLIENT_ID` in the `.env`)
- **OIDC Redirect URIs** includes **exactly** `https://it-inventory.kpndomain.com/auth/sso/callback`
  (HTTPS — the production callback; no trailing slash)
- **Target URL** = `https://it-inventory.kpndomain.com/`

The Hub tile drives an IdP-initiated launch: the Hub runs `authorize` with the user's Hub session
and redirects to the callback with `code` + `code_verifier`. Over production HTTPS that redirect is
encrypted in transit. Full behaviour and failure codes are in [`DWS_HUB_SSO.md`](DWS_HUB_SSO.md).

---

## 1. Choose free ports (do this first, on each host)

Each host runs other applications. Pick ports that are free and record them.

On **172.28.80.50 (frontend)** and **172.28.80.51 (backend)**:

```bash
sudo ss -lntp | sort -t: -k2 -n
```

Read the LISTEN list and choose:

| Setting | Purpose | Host | Must be free |
|---|---|---|---|
| `WEB_PORT` | web container publish (behind the host nginx) | FE 172.28.80.50 | pick a free high port, e.g. 8090 |
| host nginx `443` / `80` | public HTTPS / HTTP-redirect | FE 172.28.80.50 | 443 free; 80 free or already the host nginx |
| `API_PORT` | API container publish | BE 172.28.80.51 | pick a free high port, e.g. 8091 |

Confirm a specific candidate is free before using it (empty output = free):

```bash
sudo ss -lntp | grep -w 8090 || echo "8090 is free"
```

If the host's nginx already owns port 80/443, you will add a **virtual host** to it rather than
starting another web server — see step 5.

---

## 2. Get the code onto both hosts

On **both** 172.28.80.50 and 172.28.80.51:

```bash
sudo mkdir -p /opt/bc-inventory && sudo chown "$USER" /opt/bc-inventory
git clone git@github-bcinventory:dwsitproject-hub/Inventory-Item.git /opt/bc-inventory 2>/dev/null \
  || (cd /opt/bc-inventory && git fetch origin)
cd /opt/bc-inventory && git checkout main && git pull --ff-only
git rev-parse --short HEAD          # note the commit you are deploying
```

Deploy the release you tested. If production tracks a branch (e.g. a `prod` tag or `main` at the
tested commit), check that out on both hosts so they match exactly.

---

## 3. Prepare the database (once, from the backend host)

### 3.1 Create the least-privilege role (AR-06)

```bash
cd /opt/bc-inventory/deploy/db
psql -h pgm-d9jn3khh0b3907w4.pgsql.ap-southeast-5.rds.aliyuncs.com -U <privileged account> -d bcinventory \
     -v app_password='<the new bcapp_rw password>' -f create-app-role.sql
```

Pass the password **bare** in single quotes — no inner quotes (the script refuses a quoted value).
Check the output: `rolsuper` must be `f`, and the audit line must read `can_read=1, can_delete=0`.

> If the `bcinventory` database does not exist yet, create it in the console first (or with the
> privileged account), then run the script.

### 3.2 Configure the backend `.env`

```bash
cd /opt/bc-inventory/deploy/backend && cp -n .env.example .env && nano .env && chmod 600 .env
```

Set, for production:

```
DB_HOST=pgm-d9jn3khh0b3907w4.pgsql.ap-southeast-5.rds.aliyuncs.com
DB_PORT=5432
DB_NAME=bcinventory
DB_USER=bcapp_rw
DB_PASSWORD=<the new bcapp_rw password>
DB_SSLMODE=Require            # insist on TLS to the database in production
DB_AUTO_MIGRATE=false         # the app may NOT alter the schema

API_BIND=172.28.80.51         # this host's private interface
API_PORT=8091                 # the free port you chose in step 1

JWT_KEY=<openssl rand -base64 48>
JWT_LIFETIME_MINUTES=120
CORS_ORIGINS=https://it-inventory.kpndomain.com   # the production origin(s), comma-separated

# DWS Hub single sign-on. Leave SSO_ENABLED=false to launch without SSO and turn it on later.
# Issuer and callback are HTTPS in production; the client id is the production one, not the
# staging it-inventory-test.
SSO_ENABLED=true
SSO_ISSUER=https://dwshub.kpndomain.com                 # the production Hub issuer (confirm exactly)
SSO_CLIENT_ID=<production oauth_client_id from Hub>
SSO_REDIRECT_URI=https://it-inventory.kpndomain.com/auth/sso/callback
SSO_SCOPE=openid profile email

SEED_ADMIN_PASSWORD=<strong, ≥10 chars>
SEED_SITE_PASSWORD=<strong, ≥10 chars>

SMTP_HOST=<relay or blank to disable>
SMTP_PORT=25
SMTP_FROM=bc-inventory@energi-up.com
SMTP_USE_TLS=true
SMTP_USER=
SMTP_PASSWORD=
```

### 3.3 Apply the schema with the owner account

`bcapp_rw` cannot create or alter tables, so the first-time schema (and any later schema change)
is applied by the privileged account. The overrides go in the **shell, before** the command:

```bash
cd /opt/bc-inventory/deploy/backend
DB_USER=<privileged account> DB_PASSWORD='<privileged pw>' DB_AUTO_MIGRATE=true \
  docker compose run --rm api --migrate
```

It should print `[migrate] schema applied and seeded — exiting without serving.` This also seeds
the two initial accounts from `SEED_ADMIN_PASSWORD` / `SEED_SITE_PASSWORD`.

> This migrate applies the **full** schema, including the `sso_sub` column SSO needs — there is no
> separate SSO migration. (On staging that column was added after the fact and caused an `SSO-006`
> until the owner `--migrate` was run; running it here first avoids that.)

---

## 4. Start the backend (172.28.80.51)

```bash
cd /opt/bc-inventory/deploy && ./update.sh backend
```

`update.sh` pulls, rebuilds and starts the API. It binds to `API_BIND:API_PORT` from `.env`.
Confirm it is healthy and reachable **from this host**:

```bash
curl -s http://172.28.80.51:8091/api/v1/health          # -> {"status":"ok","db":true}
```

If SSO is enabled, confirm the backend reached the Hub and built its config (a failure here just
hides the button — password login still works, so investigate but it is not a launch blocker):

```bash
curl -s http://172.28.80.51:8091/api/v1/auth/sso/info    # -> enabled:true + the production Hub endpoints
```

Confirm it runs as the unprivileged user and on the least-privilege role:

```bash
cd /opt/bc-inventory/deploy && ./harden.sh
```

Expect: container user `bcinv`, `application database user is 'bcapp_rw'`, secrets `600`.

> The API must be reachable from **172.28.80.50 only**, not the whole network. `API_BIND` binds it
> to the private interface; also confirm any host firewall / security group allows 172.28.80.50 →
> 172.28.80.51 on `API_PORT` and nothing else.

---

## 5. Deploy the frontend (172.28.80.50)

### 5.1 Point the web tier at the API and choose its port

```bash
cd /opt/bc-inventory/deploy/frontend && cp -n .env.example .env 2>/dev/null; nano .env
```

Set the web container to publish on your chosen free port, bound to loopback so only the host
nginx reaches it:

```
WEB_BIND=127.0.0.1
WEB_PORT=8090
```

The web container serves the SPA and proxies `/api` **straight to the backend host by IP**. Its
nginx config is the mounted `deploy/frontend/nginx.staging.conf` (mounted by the web compose file —
the baked-in `nginx.conf` is not used in this deployment). Edit two things in it:

```bash
nano /opt/bc-inventory/deploy/frontend/nginx.staging.conf
```

- set `server_name` to the production hostname (and the FE private IP);
- set the `/api/` proxy target to the backend host and port you chose:
  `proxy_pass http://172.28.80.51:8091$request_uri;` — this must match `API_BIND:API_PORT` from 3.2.

### 5.2 Build and start the web container

```bash
cd /opt/bc-inventory/deploy && ./update.sh frontend
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8090/    # -> 200
```

This produces a **production build** — the sign-in credential hint is absent and the security
headers are served. Confirm:

```bash
curl -sI http://127.0.0.1:8090/ | grep -iE "content-security-policy|x-frame-options"
```

### 5.3 Front it with TLS on the host nginx (AR-02 — mandatory)

Production must serve HTTPS. Use the host's nginx as the public entry point.

```bash
sudo cp /opt/bc-inventory/deploy/frontend/host-nginx-vhost.conf /etc/nginx/sites-available/bc-inventory.conf
sudo nano /etc/nginx/sites-available/bc-inventory.conf
```

In that file:
- set `server_name` to the production hostname;
- set `proxy_pass http://127.0.0.1:8090;` (your `WEB_PORT`);
- **uncomment the `listen 443 ssl` server block and the HTTP→HTTPS redirect**, and point
  `ssl_certificate` / `ssl_certificate_key` at the installed cert and key;
- **uncomment the `Strict-Transport-Security` header** (it ships commented in both the host vhost
  and `nginx.staging.conf`) now that TLS terminates in front.

> Two nginx layers carry the security headers: the host vhost (public) and the web container's
> `nginx.staging.conf` (internal). Uncomment HSTS in both so it is present whichever layer answers.

```bash
sudo ln -s /etc/nginx/sites-available/bc-inventory.conf /etc/nginx/sites-enabled/ 2>/dev/null
sudo nginx -t && sudo systemctl reload nginx
```

Confirm the public endpoint and that HTTP redirects:

```bash
curl -sI https://it-inventory.kpndomain.com/ | head -1                 # 200
curl -sI http://it-inventory.kpndomain.com/  | grep -i location        # 301 -> https
```

---

## 6. Post-deployment verification

Run through this before announcing the URL:

- [ ] `curl https://<host>/api/v1/health` → `{"status":"ok","db":true}`
- [ ] Sign in as the seeded admin; the **credential hint is not shown** on the login page.
- [ ] The four Admin tabs load; the user list shows the two seeded accounts and nothing stale.
- [ ] Open Reports and Inventory Movement; a report loads and a period filter works.
- [ ] Response headers include `Content-Security-Policy` and `Strict-Transport-Security`.
- [ ] `harden.sh` on the backend host reports **No issues found**.
- [ ] The Audit Log shows the deployment’s first logins.
- [ ] Try a wrong password five times → the account locks (throttle live).
- [ ] The API host is **not** reachable from a machine other than 172.28.80.50.

If SSO is enabled, also:

- [ ] `curl https://<host>/api/v1/auth/sso/info` → `enabled:true` with the production Hub endpoints.
- [ ] The **Sign in with DWS Hub** button appears on the login page.
- [ ] Create one real Hub user's email as an **active** account (Admin → User Management) — SSO does
      **not** auto-create accounts; an unmatched Hub user is refused with `SSO-005`.
- [ ] Click the **IT Inventory tile in the Hub** → you land in the app; the Audit Log records
      `auth.sso_login`. (A `SSO-006` here means the schema migrate was skipped; `SSO-005` means the
      email is not an active account.)
- [ ] Password sign-in still works for an account with no Hub identity (e.g. the Super Admin).

Then **rotate the seeded admin password** from a strong one you set, and confirm the old one no
longer works.

---

## 7. First data load

Upload the current period’s extracts through the Ingestion page (do not copy staging data). Confirm
each identifies as the right template and the row counts match the files.

---

## 8. Rollback

If a release misbehaves, roll the code back on the affected host and rebuild:

```bash
cd /opt/bc-inventory && git checkout <previous good commit> && cd deploy && ./update.sh <tier>
```

The database is not rolled back by this. A schema change is forward-only; if one caused the
problem, restore from an ApsaraDB backup/snapshot taken **before** the migration (take one in the
console before step 3.3 for exactly this reason).

---

## 9. Routine operations

| Task | Command (on the relevant host) |
|---|---|
| Deploy an update | `cd /opt/bc-inventory && git pull --ff-only && cd deploy && ./update.sh {backend\|frontend}` |
| Apply a schema change | owner-account `--migrate` (step 3.3) **before** starting the new backend |
| Health/hardening check | `deploy/harden.sh` |
| Dependency/image scan | `deploy/scan-dependencies.sh` |
| Watch logs | `docker compose -f deploy/<tier>/docker-compose.yml logs -f` |

**Never** run `reset-transaction-data.sh` against production unless you intend to erase all
uploaded data — it is a testing tool. It reads the live connection from `deploy/backend/.env`, so
on a production host it targets production.

---

## Port summary (fill in the ports you chose)

| Host | Port | Bound to | Purpose |
|---|---|---|---|
| FE 172.28.80.50 | 443 | 0.0.0.0 | public HTTPS (host nginx) |
| FE 172.28.80.50 | 80 | 0.0.0.0 | HTTP → HTTPS redirect |
| FE 172.28.80.50 | `WEB_PORT` (e.g. 8090) | 127.0.0.1 | web container (host nginx proxies to it) |
| BE 172.28.80.51 | `API_PORT` (e.g. 8091) | 172.28.80.51 | API container (frontend host only) |
| ApsaraDB | 5432 | instance | database (whitelist FE + BE) |

Confirm each host port is free with `sudo ss -lntp | grep -w <port>` before binding it.
