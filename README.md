# BC Inventory Reporting System

Customs inventory reporting for a bonded zone (TPB / Pusat Logistik Berikat), replacing the
legacy Web IT Inventory / BC Reports. Vendor customs and stock extracts are uploaded, parsed
into a common model, and browsed as governed reports with column control, sorting, export,
reconciliation and an append-only audit trail.

React SPA + ASP.NET Core 8 API + PostgreSQL 16 (ApsaraDB RDS in staging). Field names everywhere
are the exact upload-template headers (PRD, Appendices D & E).

## Run locally

```bash
docker compose up --build -d
```

Then open **http://localhost:8088**

| Account | Password | Role / scope |
|---|---|---|
| admin@energi-up.com | Admin123! | Super Admin, all entities |
| bc.bontang@energi-up.com | Bontang123! | Site BC User, PT EUP · Bontang (scope-locked) |

These local-dev defaults, and the credential hint on the sign-in page, appear **only in a
development build**. A production build hides both. On first startup the API auto-ingests the
sample extracts from `docs/` through the real parser pipeline, so every screen is populated.

## Reports

Fourteen reports across two pages. Twelve own an upload template; two (Laporan Sparepart and
Laporan Aset) are **views** over the Aset dan Sparepart upload rather than separate files.

| Report | Template | Page | Format / note |
|---|---|---|---|
| Pemasukan Barang — PIB Import (BC 2.3) | BC23 | Reports | tab-separated text (.xls) |
| Pemasukan Barang — BC 4.0 | BC40 | Reports | HTML table (.xls) |
| Pengeluaran Barang — BC 3.0 (PEB) | BC30 | Reports | XLSX, 2-row merged header |
| Pemasukan Barang — BC 2.7 | BC27IN | Reports | XLSX |
| Pengeluaran Barang — BC 2.7 | BC27OUT | Reports | XLSX — shared layout |
| Pengeluaran Barang — BC 2.5 | BC25 | Reports | XLSX — shared layout |
| Pengeluaran Barang — BC 4.1 | BC41 | Reports | XLSX — shared layout |
| Laporan WIP | WIP | Inventory Movement | XLSX |
| Laporan Bahan Baku dan Bahan Penolong | BAHANBAKU | Inventory Movement | XLSX; also shows Kode Barang 912 from the Aset file |
| Laporan Barang Jadi | BARANGJADI | Inventory Movement | XLSX — shared layout with Bahan Baku |
| Laporan Aset dan Sparepart | ASET | (upload only) | XLSX upload template; read as the two views below |
| Laporan Sparepart | ASET (view) | Inventory Movement | Kode Barang 9xx except 912 |
| Laporan Aset | ASET (view) | Inventory Movement | Kode Barang not starting with 9 |
| Laporan Scraps | SCRAP | Inventory Movement | XLSX — shared layout with Aset dan Sparepart |

**Aset / Sparepart routing.** The vendor uploads one *Laporan Aset dan Sparepart* file; its rows
are split by Kode Barang prefix — `9xx` except `912` → Sparepart, `912` (bahan penolong) →
Bahan Baku dan Bahan Penolong, everything else → Aset. A row with no code stays in Aset rather
than vanishing. The three views always reconcile to the uploaded row count.

**Template identification.** Templates are identified from the file's own header row, not its name
or extension. Byte-identical layouts — Bahan Baku / Barang Jadi, Aset dan Sparepart / Scraps, and
BC 2.7 Out / BC 2.5 Out / BC 4.1 Out — fall back to the sheet name, file name or title row; when
none names the report the upload is rejected with an explanation rather than guessed. The five
movement templates carry a leading **Tanggal Transaksi** column. The Ingestion page lists every
upload template with its columns and load count, and offers a blank `.xlsx` per report (with a
"Petunjuk" sheet) to fill in and upload back; columns match by header text and may be reordered.

## What's implemented

- **Auth & sessions** — JWT login (BCrypt), role + entity/site scope enforced server-side. Every
  request re-reads the account, so disabling it, demoting the role, narrowing scope or resetting
  the password ends the session within seconds rather than at token expiry. Token lifetime 120 min
  (configurable). Sign-in is rate-limited with per-account lockout.
- **Reports & movement** — catalog + query endpoint: column projection, multi-sort, server-side
  paging, scope filter, search; field names verbatim from the upload headers. Grid: column chooser,
  click / Shift-click sort, drag-and-drop reorder, page size, saved views per user per report.
- **Ingestion** — format sniffing by content (BC23 TSV, BC40 HTML, XLSX), header-fingerprint
  identification, quarantine with reasons, duplicate-hash idempotency, per-period upsert, manual
  upload with a 100 MB cap.
- **Dashboard** — KPIs (entity-scoped), per-month trend by template, latest ingestions.
- **Exports** — Excel (.xlsx, typed cells, text identifiers keep leading zeros) and CSV, honouring
  the grid's visible columns, order and sort. Indonesian number and date formatting throughout.
- **Notifications** — in-app bell + e-mail. Per-user, per-event routing managed in Administration →
  Notifications: each user × event (File ingested / Rows quarantined / Ingestion failed / Security)
  with independent in-app and e-mail channels; users with no explicit setting fall back to the
  event's default roles. E-mail is sent over STARTTLS when a relay is configured.
- **Admin** — user management (create with role + scope, disable/enable, password reset; no hard
  delete), master data (entities / sites / TPB permits) with duplicate and test-entry blocking,
  role management (role × page × view/insert/edit matrix), notification routing. Password policy:
  ≥10 chars, some variety, no obvious words or the account's own address.
- **Audit** — append-only `audit.audit_events`, enforced by database triggers that reject UPDATE,
  DELETE **and** TRUNCATE. Records logins (incl. failures with reason + IP), session rejections,
  report runs, exports, ingestion events, and every user / master-data / permission change. Viewer
  with date / actor / action / text filters and CSV export.
- **LPM / Reconciliation** — saldo per material per month (opening + in − out − adj = closing) and
  BC 4.0 goods-receipt variance flags with a tolerance check.

## Security posture

The security architecture review and its remediation are in
`docs/BC_Inventory_Security_Review_and_Pentest_Plan_v1.1.docx`; the executed pentest is in
`docs/BC_Inventory_Pentest_Execution_Report_Staging_v1.0.docx`. Highlights as built:

- Authorization is re-validated from the database per request (a token proves identity only).
- Login throttling with per-account lockout; a ≥10-char password policy.
- CORS restricted to configured origins; security response headers (CSP, X-Frame-Options,
  nosniff, Referrer-Policy) served by nginx; server version suppressed.
- The API container runs as an unprivileged user; the application connects to the database as a
  least-privilege role (`bcapp_rw`) that cannot delete from or unprotect the audit trail.
- `deploy/harden.sh` checks host secrets, file permissions and the container user;
  `deploy/scan-dependencies.sh` scans NuGet, npm and the images.
- **TLS is not yet enabled on staging** (an accepted decision); a ready-to-enable block ships in
  `deploy/frontend/host-nginx-vhost.conf`. It is mandatory before production.

## Deployment

Staging is three hosts — frontend (172.28.92.56), backend (172.28.92.57) and the database, now
**ApsaraDB RDS for PostgreSQL**. See `docs/DEPLOYMENT.md` and `docs/DB_Migration_to_ApsaraDB.md`.

- Each tier redeploys with `deploy/update.sh {db|backend|frontend}` (pull + rebuild + restart).
- The application runs as the least-privilege DB role, which may **not** alter the schema, so set
  `DB_AUTO_MIGRATE=false` in `deploy/backend/.env`. Apply schema changes with an owner account:
  `DB_USER=<owner> DB_PASSWORD=<owner-pw> DB_AUTO_MIGRATE=true docker compose run --rm api --migrate`.
- Clear transaction data between test cycles (keeping users, roles and master data) with
  `deploy/db/reset-transaction-data.sh`.

Key settings in `deploy/backend/.env` (see `.env.example`): `DB_HOST/PORT/NAME/USER/PASSWORD`,
`DB_SSLMODE`, `DB_AUTO_MIGRATE`, `JWT_KEY`, `JWT_LIFETIME_MINUTES`, `CORS_ORIGINS`,
`SMTP_HOST/PORT/FROM/USE_TLS/USER/PASSWORD`.

## Services (local)

| Service | Port | Notes |
|---|---|---|
| web (nginx + React) | 8088 | serves the SPA, proxies `/api` to the API, sets security headers |
| api (ASP.NET Core 8) | internal 8080 | JWT auth, query engine, ingestion; runs as unprivileged `bcinv` |
| db (PostgreSQL 16) | 5442 (host) | user `bcapp`, db `bcinventory` |
| mail (Mailpit) | 8025 | local inbox for alert e-mails |

## Documentation

| Document | What it is |
|---|---|
| `docs/BC_Inventory_System_PRD_v1.3.docx` | Product requirements — current system |
| `docs/BC_Inventory_System_TechDoc_v1.1.docx` | Technical design |
| `docs/BC_Inventory_User_Manual_{EN,ID}.docx` | End-user guide (English / Bahasa Indonesia) |
| `docs/BC_Inventory_Admin_Manual_{EN,ID}.docx` | Administrator guide (English / Bahasa Indonesia) |
| `docs/BC_Inventory_E2E_Test_Scenarios_v1.0.docx` | Functional test pack |
| `docs/BC_Inventory_E2E_Test_Results_v1.0.docx` | Functional test execution report |
| `docs/BC_Inventory_Security_Review_and_Pentest_Plan_v1.1.docx` | Security review + pentest plan |
| `docs/BC_Inventory_Pentest_Execution_Report_Staging_v1.0.docx` | Pentest execution report |
| `docs/DEPLOYMENT.md`, `docs/DB_Migration_to_ApsaraDB.md` | Deployment & DB migration runbooks |

## Useful commands

```bash
docker compose logs -f api        # watch ingestion / query logs
docker compose down               # stop (keeps DB volume)
docker compose down -v            # stop and wipe the database
```

## MVP simplifications (documented deviations from the TechDoc)

- Upload fields live in one `data jsonb` column per line (exact-name keys) rather than typed
  extension tables; the field catalog casts on read.
- Offset paging (fine at test volume) rather than keyset; JWT held in sessionStorage
  (production hardening: httpOnly refresh token) — see the security review.
