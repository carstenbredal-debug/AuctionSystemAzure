# KSeF Integration — Environments & Operations

Reference for the KSeF environments, apps, pages and endpoints (state: 2026-08-26).
Provisioning/promotion checklists live in [TEST_PROD_KSEF.md](TEST_PROD_KSEF.md).

## 1. Environments at a glance

| | DEV | TEST | PROD |
|---|---|---|---|
| Function App | `KSEFIntegration` | `KSEFIntegrationtest` | `func-ksef-prod-86679` |
| Resource group | (legacy subscription `f358fb73…`) | `Auction_Test` | `Auction_Production` |
| Git branch | `dev` | `test` | `prod` |
| Deploy workflow | `ksef-deploy-functions-dev.yml` | `deploy-ksef-functions-only-test.yml` | `Deploy KSeF Functions (PROD)` |
| KSeF backend | demo (`api-demo.ksef.mf.gov.pl`) | demo | **production** `https://api.ksef.mf.gov.pl/v2` |
| App Insights | per app, same name | `KSEFIntegrationtest` | `func-ksef-prod-86679` |

- Company NIP: **5253073718**. PROD auth = KSeF token in app setting `KSeF__Token`
  (issued 2026-06-29); backend URL in `KSeF__BaseUrl`.
- Promotion is by **merge commits**: `dev` → `test` ("Promote dev to test: …") →
  `prod` ("Promote test to prod: …"). Workflows are path-filtered on
  `ksef/KSeF.Functions/**`, so a prod push deploys only when KSeF code changed.
- **History note:** everything "Accepted" before 2026-07-06 was submitted to the
  DEMO environment only; production submissions start 2026-07-06.

## 2. Browser pages (function-key in the URL: `?code=<host default key>`)

| Page | Route | Purpose |
|---|---|---|
| Incoming invoices | `/api/purchases/view?nip=5253073718` | Purchase invoices (Subject2) from KSeF: date range, incoming/outgoing toggle, totals per currency, filter, CSV, per-row **pdf / xml / view** |
| JPK_V7M editor | `/api/jpk/editor` | Load BC's JPK_V7M(3) XML, edit the full VAT-7(23) declaration (incl. refund request P_54 + deadline choice), live ministry-formula checks, XSD validation, download final XML |
| JPK visualizer | `/api/jpk/visualizer` | Read-only JPK inspection, consistency checks, two-file diff |

The pages are served with `Cache-Control: no-cache`, and their row links go through
the **same function** (`?show=` / `?xml=` / `?pdf=` on `/api/purchases`) because
Azure function keys are per-function — links to other functions 401 silently.

## 3. API endpoints (KSeF.Functions)

| Route | What it does |
|---|---|
| `POST /api/invoice/submit` | Submit invoice/credit memo to KSeF (BC extension calls this) |
| `GET /api/invoice/status/{elementRef}?nip=&sessionRef=` | Status of a prior submission (needs the stored refs) |
| `GET /api/invoice/lookup?nip=&invoiceNumber=&dateFrom=&dateTo=` | **Find an invoice by number** via the metadata query — no refs needed; powers BC "Fetch from KSeF" |
| `GET /api/invoice/view/{ksefNumber}?nip=` | Rendered HTML wizualizacja |
| `GET /api/invoice/xml/{ksefNumber}?nip=` | The legal FA(3) XML as a download |
| `GET /api/purchases?nip=&from=&to=&dateType=&subjectType=&pageOffset=` | Metadata query proxy (raw KSeF JSON passthrough); `&show=` / `&xml=` / `&pdf=` fetch one invoice as HTML / XML / QuestPDF PDF |
| `POST /api/jpk/v7m/generate` | Build JPK_V7M(3) XML |
| `POST /api/jpk/v7m/validate` | Validate XML against the embedded ministry XSDs |
| `GET /api/test-ksef-auth?nip=` | Full auth handshake test (catches a bad token) |
| `GET /api/health` | Anonymous liveness check |

## 4. KSeF API facts learned the hard way

- **Auth endpoints are rate-limited (~20 requests/window).** All query/read paths
  share a **cached access token per NIP (10 min, static in `KSeFApiClient`)** and
  re-auth once on 401. Never add a code path that authenticates per item.
- **`pageOffset` on `/invoices/query/metadata` is a PAGE INDEX** (first item =
  `pageOffset × pageSize`); items ≥ 10 000 are refused — narrow the date filter.
- **The FA(3) XML is the legal invoice.** KSeF stores no PDF; every PDF is a
  rendering. Verification URL: `https://ksef.mf.gov.pl/web/verify/{ksefNumber}/{sha256-hex-of-xml}`
  (also encoded in the QR on our HTML/PDF wizualizacja).
- Submission still opens **one KSeF session per invoice** (~1/min pacing, ~120
  session-opens/h per NIP) — the planned session-reuse rework is the open
  scalability item before high-volume periods.

## 5. Business Central side

- Extension **KPHG Auction Compliance** (currently 2.36), objects 50200–50399.
  CI build of the extension is flaky — **build locally** with alc.exe
  (`_altool`, symbols in `KSeF.BCExtension/.alpackages`) and upload the .app via
  Extension Management.
- Setup: **KSeF Setup** page → Azure Function URL (`https://func-ksef-prod-86679.azurewebsites.net/api`),
  function key, Company NIP, Environment = Production. Dispatch job = recurring
  Job Queue Entry (1 min, ≤30 docs/type/run) sending `Ready`/`Error` documents.
- Document statuses: `Ready → Processing → Sent → Accepted` (+ `Rejected`,
  retryable `Error`, max 5 attempts). Repair actions on KSeF Invoices /
  posted-document pages:
  - **Reset KSeF Status** — re-queue; refuses documents that have a KSeF number
    or an in-flight submission (Sent, or Processing younger than 30 min).
  - **Mark as Accepted** — manual number entry for documents verified in KSeF.
  - **Fetch from KSeF** — looks the document up by invoice number and backfills
    number / QR / acceptance date (multi-select capable).
- **Posted tables have entitlement Modify = Indirect** — config packages /
  RapidStart / Edit-in-Excel cannot write Sales Invoice Header regardless of
  SUPER + Premium; only extension code (with `Permissions = tabledata … = M`)
  can. That is why the repair actions exist.

## 6. Monitoring

Application Insights per app; useful KQL against `func-ksef-prod-86679`:

```kusto
traces | where message startswith "Generated FA(3)" or message startswith "KSeF accepted"
       | order by timestamp desc           // submissions and their KSeF numbers
requests | summarize count() by name, resultCode, bin(timestamp, 1d)   // endpoint health
traces | where message contains "duplicate"                            // duplicate-path hits
```

A submission logs `Generated FA(3) XML for invoice {no}` and, on success,
`KSeF accepted on attempt N: {ksefNumber}` under one `operation_Id` — join on it
to map invoice numbers to KSeF numbers (used for the 2026-08 backfill).
