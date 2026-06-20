# KSeF TEST / PROD Promotion & Setup

Companion to AuctionSystemAzure's `TEST_PROD_PROVISIONING.md`. That runbook covers the auction
system; this covers the **KSeF integration** (this repo: the `KSeF.Functions` Azure Function + the
`KPHG Auction Compliance` BC extension). Same model: 3 branches, controlled promotion.

- **Branch → env:** `dev` → DEV, `test` → TEST, `prod` → PROD.
  - Function deploys: `deploy-functions.yml` (dev), `deploy-functions-test.yml`, `deploy-functions-prod.yml`.
  - BC extension build (`build-bc-extension.yml`) runs on all three; it produces the `.app` artifact —
    **publishing to BC is manual** (grab the artifact, publish into that environment's BC company).
- **Do TEST end-to-end first, prove it, then PROD.**

---

## 1. Azure resources (per environment)

Each environment gets its **own** KSeF Function App (separate from the auction Function App):

- [ ] **Function App** (Flex Consumption, .NET 8 isolated), one per env.
- [ ] **GitHub secrets** (this repo) for the deploy workflows:
      `AZURE_CLIENT_ID_TEST` / `_PROD`, `AZURE_SUBSCRIPTION_ID_TEST` / `_PROD`, `AZURE_TENANT_ID`,
      `AZURE_FUNCTIONAPP_NAME_TEST` / `_PROD`, `AZURE_RESOURCE_GROUP_TEST` / `_PROD`.
      (OIDC federated login — no stored credentials.)

## 2. Function App settings (per environment) — the KSeF environment switch

The function talks to the **right KSeF backend via app settings**, NOT code. Set per Function App:

| Setting | DEV / TEST | PROD |
|---|---|---|
| `KSeF:BaseUrl` | `https://ksef-test.mf.gov.pl/api` (or `-demo`) | `https://ksef.mf.gov.pl/api` |
| KSeF auth token / cert | Test token | **Production authorization (see §4)** |

- [ ] Store the KSeF auth secret in the Function App config / Key Vault — **never in the repo**.

## 3. BC extension (per environment)

- [ ] Publish the `KPHG Auction Compliance` `.app` into each env's BC company.
- [ ] On a fresh company the install codeunit **auto-seeds `Environment = Test` + `Default KSeF Required = true`**
      and creates the dispatch Job Queue Entry. **For PROD this is wrong by default** — after publishing to
      the production company you MUST set **`KSeF Environment = Production`**, the **production Company NIP**,
      and the **Azure Function URL** (the PROD function, **with the `/api` suffix**) + **Function Key**.
- [ ] Then **Test Connection**, and confirm the **dispatch job** is `Ready` (or click "Activate KSeF Dispatch Job").

## 4. ⚠️ PROD long-pole — real KSeF authorization

PROD = **legally binding e-invoices to the Polish tax authority.** This needs the company's
**production KSeF authorization** (KSeF token / qualified certificate / ePUAP-trusted profile for the
NIP). This is an **external/compliance dependency with lead time** — start it early, in parallel with
the Azure work. Do **not** point PROD at the production KSeF URL until this is in place and the NIP is correct.

## 5. Throughput — the 120/hour limit (matters in PROD)

KSeF caps **session-opens at ~120/hour per NIP**, and the function currently opens **one session per
invoice**. The paced dispatch (≈ batch/min) keeps DEV/TEST under control, but a real settlement-day
burst will hit the cap. **Before heavy PROD volume**, do the **session-reuse rework** (open one session,
submit many invoices, close) + **429 back-off** in the dispatch (honor KSeF's "retry after N min").
Tracked as the next code task.

## 6. Verification (per environment)

- [ ] `GET <funcapp>/api/health` → `{"status":"healthy"}` (anonymous).
- [ ] BC **Test Connection** → success.
- [ ] Post one invoice → `Ready → Processing → Accepted` with a KSeF number.
- [ ] Post one credit memo → `Accepted` (FA(3) `DaneFaKorygowanej` valid).
- [ ] Confirm it lands in the matching **KSeF web portal** (`ksef-test...` / `ksef...` `/web/`).
- [ ] KSeF Transfer tiles on the Setup page reflect the counts.
