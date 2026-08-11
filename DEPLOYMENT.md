# BC Inventory Reporting System — Staging Deployment Guide

Three-tier deployment to the Alibaba Cloud staging servers, using PuTTY + Docker Compose.
Every command below is copy-paste ready. Run them **as `root`** in the PuTTY session for the
server named in each section heading.

---

## 1. Topology & port allocation

```
   Browser  ──http──▶  FE 172.28.92.56 : 8088   (nginx + React SPA)
                              │  proxies /api/
                              ▼
                       BE 172.28.92.57 : 8085   (ASP.NET Core API)
                              │  SQL
                              ▼
                       DB 172.28.92.60 : 5443   (PostgreSQL 16)
```

Ports were chosen against what is **already running** on each host (`docker ps`, 11 Aug 2026):

| Server | In use today | **BC Inventory uses** |
|---|---|---|
| DB 172.28.92.60 | 5432 jps-db · 5433 cloud-agent · 5434 overtime · 5435 downstream-hub · 5436 pct · 5440 project-mgmt · 5442 klip | **5443** |
| BE 172.28.92.57 | 3000 jps-api · 3001 slms · 3003 eos · 4000 downstream-hub · 4100 pct · 5000 overtime · 5001 klip · 5002 crc · 5050 cloud-agent · 5422 eos-pg · 5442 klip-pg · 5544 slms-pg · 13000 project-mgmt | **8085** |
| FE 172.28.92.56 | 3000 slms · 3001 klip · 3020 eos · 3030 project-mgmt · 3042 overtime · 3050 pct · 3080 jps · 3090 msd · 3100 downstream-hub · 5432 klip-pg · 8010 crc | **8088** |

> Re-check with `docker ps --format '{{.Names}}\t{{.Ports}}'` before you start — if any of
> 5443 / 8085 / 8088 has been claimed since, change it in that tier's `docker-compose.yml`
> **and** in `deploy/frontend/nginx.staging.conf` (which hard-codes the API address).

**Binding is deliberate.** The database binds to `172.28.92.60` and the API to `172.28.92.57`
— the private interface only, never `0.0.0.0`. Only the web tier binds `0.0.0.0:8088`, because
it is the sole port users need. Do not "simplify" these to `0.0.0.0`.

---

## 2. Prerequisites

On each server:

```bash
docker --version && docker compose version && git --version && nc -zv github.com 22 && df -h / && free -m
```

Docker Engine 20.10+ with the Compose v2 plugin, plus `git` and outbound SSH to GitHub. All
three hosts already run Docker, so this is a confirmation step, not an install step. If `git`
is missing: `apt-get update && apt-get install -y git`. If GitHub port 22 is unreachable, see
§3.3 for the file-transfer fallback.

### Security-group rules (Alibaba Cloud ECS)

Add these in the ECS console — the app will not work without them, and each rule is
deliberately narrow:

| Direction | Port | Source | Purpose |
|---|---|---|---|
| Inbound on **DB** 172.28.92.60 | 5443/tcp | `172.28.92.57/32` | API → PostgreSQL |
| Inbound on **BE** 172.28.92.57 | 8085/tcp | `172.28.92.56/32` | nginx → API |
| Inbound on **FE** 172.28.92.56 | 8088/tcp | your office / VPN CIDR | users → app |

Do **not** open 5443 or 8085 to `0.0.0.0/0`. The database port in particular should only ever
be reachable from the backend server.

---

## 3. Get the code from GitHub

Source of truth: **`git@github.com:dwsitproject-hub/Inventory-Item.git`** (branch `main`).

The repository is private, so each server needs its own **read-only deploy key**. Run §3.1 and
§3.2 **once per server** — on all three (172.28.92.60, .57, .56).

> **Why a key per server, generated on the server?** The private key is created on the machine
> that uses it and never travels anywhere, so there is nothing to intercept. Each key is
> read-only, so a compromised staging host can pull but can never push to your repository, and
> you can revoke one host without disturbing the other two. Do **not** copy the key from your
> Windows machine — that one has write access.

### 3.1 Generate the server's deploy key

```bash
ssh-keygen -t ed25519 -C "bc-inventory deploy $(hostname -I | awk '{print $1}')" -f ~/.ssh/id_ed25519_bcinv -N "" && cat ~/.ssh/id_ed25519_bcinv.pub
```

Copy the printed line. In GitHub open
**https://github.com/dwsitproject-hub/Inventory-Item/settings/keys** → *Add deploy key*:

* **Title** — name the host, e.g. `staging-db 172.28.92.60`
* **Key** — paste the line
* **Allow write access** — leave **unticked**. Deployment only ever reads.

### 3.2 Configure SSH and clone

```bash
ssh-keyscan -t ed25519 github.com >> ~/.ssh/known_hosts 2>/dev/null
cat >> ~/.ssh/config <<'EOF'

Host github-bcinv
  HostName github.com
  User git
  IdentityFile ~/.ssh/id_ed25519_bcinv
  IdentitiesOnly yes
EOF
chmod 600 ~/.ssh/config ~/.ssh/id_ed25519_bcinv
ssh -T git@github-bcinv
```

The last command should answer `Hi dwsitproject-hub/Inventory-Item! You've successfully
authenticated…`. If it says *permission denied*, the deploy key was not added (or was added to
the wrong repository).

Now clone:

```bash
git clone git@github-bcinv:dwsitproject-hub/Inventory-Item.git /opt/bc-inventory && cd /opt/bc-inventory && git log --oneline -1
```

Note the commit hash — that is what this host is running.

> The DB server only needs `deploy/db`, but cloning the whole repository on all three keeps the
> hosts identical and makes `git pull` the single upgrade procedure everywhere. The clone is
> small: the sample customs extracts are deliberately **not** in the repository (they hold real
> vendor names, NPWP tax IDs and transaction values), so `docs/` contains only the written
> specifications.

### 3.3 If the servers cannot reach GitHub

Some hardened VPCs block outbound 22. Test with `nc -zv github.com 22`. If it fails, either
allow outbound SSH to GitHub in the security group, or fall back to a one-off file transfer
from your Windows machine:

```bash
tar --exclude=node_modules --exclude=.git --exclude=frontend/dist -czf bc-inventory.tar.gz backend frontend deploy README.md DEPLOYMENT.md
pscp bc-inventory.tar.gz root@172.28.92.60:/opt/
```

…then `mkdir -p /opt/bc-inventory && tar -xzf /opt/bc-inventory.tar.gz -C /opt/bc-inventory`.
With this fallback you lose `git pull` upgrades and must repeat the transfer each release.

---

## 4. Deploy the DB tier — 172.28.92.60

```bash
cd /opt/bc-inventory/deploy/db && cp -n .env.example .env && openssl rand -base64 24
```

Put that generated string into `.env` as `DB_PASSWORD` (keep it — the API server needs the
same value):

```bash
nano /opt/bc-inventory/deploy/db/.env
```

Start it:

```bash
cd /opt/bc-inventory/deploy/db && mkdir -p backups && docker compose up -d && sleep 15 && docker compose ps
```

Verify the database is healthy and listening on the right interface:

```bash
docker exec bc-inventory-postgres pg_isready -U bcapp -d bcinventory && ss -lntp | grep 5443
```

Expected: `accepting connections`, and a listener on `172.28.92.60:5443` (**not** `0.0.0.0`).

---

## 5. Deploy the API tier — 172.28.92.57

```bash
cd /opt/bc-inventory/deploy/backend && cp -n .env.example .env && openssl rand -base64 48
```

Edit `.env` and set **all five** required values — the container refuses to start if any is
missing, which is intentional:

```bash
nano /opt/bc-inventory/deploy/backend/.env
```

* `DB_PASSWORD` — exactly what you set on the DB server
* `JWT_KEY` — the 48-char string just generated
* `SEED_ADMIN_PASSWORD`, `SEED_SITE_PASSWORD` — initial login passwords, **not** the
  documented local-dev ones
* `SMTP_HOST` — your relay, or leave empty to disable e-mail (in-app alerts still work)

Confirm the API server can actually reach the database before building:

```bash
nc -zv 172.28.92.60 5443
```

If that times out, the security-group rule from §2 is missing — fix it first.

Build and start (first build pulls the .NET SDK image and takes a few minutes):

```bash
cd /opt/bc-inventory/deploy/backend && docker compose up -d --build && sleep 40 && docker compose logs --tail 40 api
```

You are looking for `[seed] master data + 2 users created … passwords from Seed__AdminPassword`
and `Now listening on: http://[::]:8080`. Then:

```bash
curl -s http://172.28.92.57:8085/api/v1/health
```

Expected: `{"status":"ok","db":true}`.

---

## 6. Deploy the Web tier — 172.28.92.56

Confirm the API address in the nginx config matches §1 (change it here if you moved the port):

```bash
grep proxy_pass /opt/bc-inventory/deploy/frontend/nginx.staging.conf
```

Check reachability, then build and start:

```bash
nc -zv 172.28.92.57 8085
cd /opt/bc-inventory/deploy/frontend && docker compose up -d --build && sleep 20 && docker compose ps
```

Verify nginx serves the SPA and proxies the API correctly from this host:

```bash
curl -s -o /dev/null -w 'spa %{http_code}\n' http://172.28.92.56:8088/ && curl -s http://172.28.92.56:8088/api/v1/health
```

Expected: `spa 200` and `{"status":"ok","db":true}`. If the SPA returns 200 but the API call
fails, the problem is between FE and BE (security group or `proxy_pass`), not the app.

---

## 7. Smoke test

Open **http://172.28.92.56:8088** and sign in as `admin@energi-up.com` with your
`SEED_ADMIN_PASSWORD`. Then check, in order:

1. **Dashboard** loads (zeros are correct — staging starts empty unless you mounted `docs`)
2. **Ingestion & Upload** → *Supported upload templates* lists all 8 templates
3. Download a blank template (`⭳ .xlsx`), fill one row, upload it → *loaded*
4. **Reports** / **Inventory Movement** → the uploaded row appears; check the elapsed-ms figure
5. **Administration → Role Management** → the matrix renders, Super Admin row locked
6. **Audit Log** → your login, the upload and the template download are all recorded

From the command line:

```bash
TOKEN=$(curl -s -X POST http://172.28.92.56:8088/api/v1/auth/login -H 'Content-Type: application/json' -d '{"email":"admin@energi-up.com","password":"YOUR_SEED_ADMIN_PASSWORD"}' | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p') && curl -s http://172.28.92.56:8088/api/v1/reports -H "Authorization: Bearer $TOKEN" | head -c 200
```

---

## 8. Post-deployment hardening

Do these on the first day, not "later":

1. **Rotate the seeded accounts.** Sign in as admin → *Administration → User Management* →
   create the real named users with proper roles and scope, then **disable**
   `bc.bontang@energi-up.com` if it is not a real person. Accounts are never hard-deleted, so
   disabling preserves the audit trail.
2. **Review Role Management** against how your team actually works. Defaults follow PRD §6.4.
3. **Restrict 8088** in the security group to your office/VPN range. Staging holds real
   customs data.
4. **Schedule backups** (§9) and confirm a restore works *before* anyone relies on the data.
5. **Keep `.env` files out of shared folders** — they hold the DB password and JWT key.
   `chmod 600 /opt/bc-inventory/deploy/*/.env`

> **HTTPS.** This guide deploys plain HTTP on the private network. Before any wider rollout,
> put the web tier behind the corporate load balancer or an nginx TLS terminator — the PRD
> requires HTTPS everywhere, and JWTs travel in the `Authorization` header.

---

## 9. Operations

### Logs

```bash
docker compose -f /opt/bc-inventory/deploy/backend/docker-compose.yml logs -f --tail 100 api
```

All three tiers cap logs at 10 MB × 5 files, so they cannot fill the disk.

### Backup (run on the DB server; add to cron)

```bash
docker exec bc-inventory-postgres pg_dump -U bcapp -d bcinventory -Fc -f /backups/bcinv_$(date +\%Y\%m\%d_\%H\%M).dump && ls -lh /opt/bc-inventory/deploy/db/backups | tail -5
```

Nightly at 01:30:

```bash
(crontab -l 2>/dev/null; echo '30 1 * * * docker exec bc-inventory-postgres pg_dump -U bcapp -d bcinventory -Fc -f /backups/bcinv_$(date +\%Y\%m\%d).dump && find /opt/bc-inventory/deploy/db/backups -name "bcinv_*.dump" -mtime +14 -delete') | crontab -
```

### Restore

```bash
docker exec -i bc-inventory-postgres pg_restore -U bcapp -d bcinventory --clean --if-exists /backups/bcinv_YYYYMMDD.dump
```

### Upgrade to a new release

Back up first (§ Backup), then on the affected tier — one command:

```bash
/opt/bc-inventory/deploy/update.sh backend
```

`update.sh` pulls `main`, prints the commit it moved to, rebuilds that tier and tails the log.
Pass `db`, `backend` or `frontend`. Typically you upgrade **backend then frontend**; the DB tier
only changes when its compose file does.

Doing it by hand is the same three steps:

```bash
cd /opt/bc-inventory && git pull && cd deploy/backend && docker compose up -d --build && docker compose logs --tail 30 api
```

Schema changes apply automatically at API start — `Db.EnsureCreated` is idempotent DDL, and the
seed only runs on an empty user table, so an upgrade never touches existing accounts or data.

To see exactly what is deployed on a host:

```bash
cd /opt/bc-inventory && git log --oneline -1 && git status -sb | head -1
```

> `git pull` refuses to overwrite local edits. Config you are meant to change (`.env` files) is
> gitignored and therefore safe. If you edited a tracked file in place, `git stash` it first —
> and then move that change into the repository, or the next upgrade will drop it again.

### Deploy a specific release rather than latest `main`

For a controlled staging cut, tag the release from your Windows machine:

```bash
git tag -a staging-2026.08.11 -m "staging cut" && git push origin staging-2026.08.11
```

Then on each server:

```bash
cd /opt/bc-inventory && git fetch --tags && git checkout staging-2026.08.11 && cd deploy/backend && docker compose up -d --build
```

### Rollback

Check out the previous commit or tag and rebuild — the fastest, most reliable path:

```bash
cd /opt/bc-inventory && git log --oneline -5
```

```bash
cd /opt/bc-inventory && git checkout <previous-commit-or-tag> && cd deploy/backend && docker compose up -d --build
```

Return to the tip later with `git checkout main && git pull`.

**A code rollback does not undo a database migration.** The schema is additive (new tables and
columns only, never drops), so an older build runs fine against a newer schema. If a release
ever needs a destructive change, restore the pre-upgrade backup as well.

### Restarting a tier

```bash
docker compose -f /opt/bc-inventory/deploy/backend/docker-compose.yml restart api
```

The web tier re-resolves the API address per request, so restarting the API does **not**
require restarting nginx.

---

## 10. Troubleshooting

| Symptom | Likely cause | Check |
|---|---|---|
| API container restarts in a loop | a required `.env` value is missing | `docker compose logs api` — the variable is named in the error |
| `502 Bad Gateway` from the SPA | FE cannot reach BE:8085 | `nc -zv 172.28.92.57 8085` from the FE server; then the security group |
| API healthy but `"db":false` | wrong password, or BE→DB blocked | `nc -zv 172.28.92.60 5443`; compare `DB_PASSWORD` on both hosts |
| Login returns 401 with the seeded password | seed ran earlier with different values | the seed only runs once — reset via a Super Admin, or wipe the volume (destroys data) |
| Blank page, console 404 on `/assets/…` | browser cached an old bundle | hard-refresh; `index.html` is served `no-store` so this self-corrects |
| Upload rejected: "matches … equally" | look-alike template, name gives no hint | keep "Bahan Baku" / "Barang Jadi" / "Aset" / "Scraps" in the file or sheet name |
| Everything 403 after a role change | permissions edited too tightly | a Super Admin can always reach *Administration → Role Management* to undo it |
| `git clone`/`pull`: Permission denied (publickey) | deploy key not added, or added to the wrong repo | `ssh -T git@github-bcinv` — it must greet you with the repo name |
| `git pull`: "local changes would be overwritten" | a tracked file was edited on the server | `git status`; move the change into the repo, or `git checkout -- <file>` to discard |
| `update.sh`: "missing .env" | tier configured but never given its secrets | `cp .env.example .env && nano .env` in that tier's folder |
| Hosts running different code | one server was never pulled | `git log --oneline -1` on each — all three should match |

### Full reset of staging data (destructive)

```bash
cd /opt/bc-inventory/deploy/db && docker compose down -v && docker compose up -d
```

This deletes the database volume, including users and the audit trail. The API re-seeds on its
next restart.

---

## 11. What each tier contains

| Tier | Container | Image | Data |
|---|---|---|---|
| DB | `bc-inventory-postgres` | `postgres:16-alpine` | volume `bcinventory-db_bcinv_pgdata` + `./backups` |
| BE | `bc-inventory-api` | built from `backend/` | volume for DataProtection keys |
| FE | `bc-inventory-web` | built from `frontend/` | stateless; config mounted from `nginx.staging.conf` |

Only the DB tier holds state. The API and web tiers can be destroyed and rebuilt at any time
without data loss.
