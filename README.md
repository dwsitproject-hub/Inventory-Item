# BC Inventory Reporting System — local Docker test build

Vertical-slice MVP of the system specified in `docs/BC_Inventory_System_TechDoc_v1.1.docx`
(React SPA + ASP.NET Core 8 API + PostgreSQL 16). Field names everywhere are the exact
upload template headers (PRD v1.2, Appendices D & E).

## Run

```bash
docker compose up --build -d
```

Then open **http://localhost:8088**

| Account | Password | Role / scope |
|---|---|---|
| admin@energi-up.com | Admin123! | Super Admin, all entities |
| bc.bontang@energi-up.com | Bontang123! | Site BC User, PT EUP · Bontang (scope-locked) |

On first startup the API auto-ingests the real sample extracts from `docs/` through the real
parser pipeline, so every screen is populated immediately.

| Template | File | Real format | Appears in |
|---|---|---|---|
| BC23 | BC23 Report.xls | tab-separated text | Reports |
| BC40 | BC40 Report.xls | HTML table | Reports |
| BC30 | Laporan BC3.0.xlsx | XLSX, 2-row merged header | Reports |
| WIP | Laporan WIP.xlsx | XLSX | Inventory Movement |
| BAHANBAKU | Laporan Bahan Baku.xlsx | XLSX | Inventory Movement |
| BARANGJADI | Laporan Barang Jadi.xlsx | XLSX | Inventory Movement |
| ASET | Laporan Aset dan Sparepart.xlsx | XLSX | Inventory Movement |
| SCRAP | Laporan Scraps ….xlsx | XLSX | Inventory Movement |
| BC27IN | Laporan BC 2.7 In.xlsx | XLSX | Reports |
| BC27OUT | Laporan BC 2.7 Out.xlsx | XLSX | Reports |
| BC25 | Laporan BC 2.5 Out.xlsx | XLSX | Reports |
| BC41 | Laporan BC 4.1 Out.xlsx | XLSX | Reports |

Templates are identified from the file's own header row, not its name or extension. Several groups
share a byte-identical layout — Bahan Baku / Barang Jadi (14 columns), Aset dan Sparepart /
Scraps (12 columns), and BC 2.7 Out / BC 2.5 Out / BC 4.1 Out (19 columns) — so those fall back
to the sheet name, file name or title row; when none of them names the report the upload is
rejected with an explanation rather than guessed at. The Ingestion page lists every supported template with its expected
columns and how many rows are loaded, and offers a **blank .xlsx template** per report
(`⭳ .xlsx`) for users to fill in and upload back. Each template ships with a "Petunjuk" sheet
documenting every column; columns are matched by header text, so they may be reordered.

## Services

| Service | Port | Notes |
|---|---|---|
| web (nginx + React build) | 8088 | serves the SPA, proxies `/api` to the API |
| api (ASP.NET Core 8) | — (internal 8080) | JWT auth, report query engine, ingestion |
| db (PostgreSQL 16) | 5442 (host) | user `bcapp`, db `bcinventory` — 5433–5440 are used by other local projects |
| mail (Mailpit) | 8025 | local inbox UI for the alert e-mails (SMTP internal :1025) |

## What's implemented (vs TechDoc)

- JWT login (local accounts, BCrypt), role + entity/site scope enforced server-side
- Report catalog + query endpoint: column projection, multi-sort, server-side paging,
  scope filter, search — field names verbatim from the upload headers
- Reports grid: column chooser (all 80/55 stored fields), click/Shift+click sort,
  drag-and-drop column reorder, reset, page-size control, elapsed-ms display
- Ingestion: format sniffing by content (FR-I8), BC23 TSV parser + BC40 HTML parser
  (merged headers, continuation rows, footer totals, `="…"` text guards — FR-I9/I11),
  quarantine with reasons (FR-I5), duplicate-hash idempotency (FR-I6), manual upload
- Dashboard: KPIs, per-month trend by template, latest ingestions
- Exports (FR-R5/R13): Excel (.xlsx, styled, typed cells, text identifiers keep leading
  zeros) and CSV — both honour the grid's visible columns, order and sort
- Saved views (FR-R12): the grid layout auto-persists per user per report and restores on
  return; named views can be saved, applied and deleted
- Notifications (FR-N subset): in-app bell with unread badge; upload/quarantine/error
  events emitted by the ingestion pipeline (role-targeted), mark-as-read; 30 s polling
- E-mail channel (FR-N1/N6): every alert also goes out via SMTP with per-recipient
  delivery status recorded — inspect at http://localhost:8025 (Mailpit)
- Admin module (FR-A1/A3/A4): user management (create with role+scope, disable/enable,
  password reset — no hard delete), master data (entities/sites/TPB permits) with
  duplicate + test-entry blocking ("TESTING" is rejected); security-change alerts
- Role Management (FR-A2): configurable role × page × (view/insert/edit) matrix, seeded from
  the PRD §6.4 capability table. Enforced by the API — a revoked page returns 403 even when
  called directly — and mirrored in the UI (nav items, export and upload buttons). Two
  invariants: Super Admin always keeps full access and its row is immutable, and only a
  Super Admin may edit the matrix (an Admin raising its own rights would be escalation).
  Every change is audited and alerts administrators.
- Audit log (FR-A7): append-only `audit.audit_events` — the table rejects UPDATE/DELETE
  via a database trigger, not just application code. Records logins (incl. failures with
  reason + IP), report runs, exports, ingestion loads/duplicates/failures, user and
  master-data changes, each with a JSON detail payload. Viewer page (Super Admin / Admin /
  Auditor) with date, actor, action and full-text filters, expandable detail, and CSV export
- LPM / Reconciliation (FR-R8): saldo per material per month
  (opening + in − out − adj = closing) and BC 4.0 goods-receipt variance flags with
  tolerance check (outbound legs are 0 until outbound extracts are specified)

## MVP simplifications (documented deviations from the TechDoc)

- Upload fields live in one `data jsonb` column per line (exact-name keys) instead of
  typed extension tables; the field catalog casts on read. Swap-in planned at build-out.
- Offset paging (fine at test volume) instead of keyset; single seeded entity;
  no SSO/MFA/alerting/exports yet; JWT in sessionStorage (production: httpOnly refresh).

## Useful commands

```bash
docker compose logs -f api        # watch ingestion / query logs
docker compose down               # stop (keeps DB volume)
docker compose down -v            # stop and wipe the database
```
