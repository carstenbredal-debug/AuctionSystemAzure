# PROD Cutover Checklist

Live tracking doc for the Production stand-up. Companion to
[`TEST_PROD_PROVISIONING.md`](TEST_PROD_PROVISIONING.md) — that's the *why*; this is the
*do-it-now, tick-as-you-go* list for **our chosen build**.

## Decisions (locked)
- **Network posture:** network-locked from day one — the **auction** Function App is never
  publicly reachable. First deploy goes through an in-VNet runner, not a GitHub-hosted one.
- **Scope:** auction **and** KSeF together.
- **KSeF prod auth:** *in progress*. Provision the KSeF Function App now but keep
  `KSeF:BaseUrl` on the **test URL**; flip to `ksef.mf.gov.pl` later (config-only change).
- **In-VNet runner:** a small **VM** in the VNet.
- **Tenant:** the separate TEST/PROD Entra tenant (no link to DEV).

## Four PROD-critical specifics (bake in, don't retrofit)
1. **Identity-based storage on BOTH Function Apps.** `AzureWebJobsStorage` + Flex deployment
   storage via **managed identity**, never a connection string / account key. (This is exactly
   what broke TEST after a storage-key rotation.) Grant each app's MI **Storage Blob Data Owner**
   on its own storage account. No `…__accountKey` settings anywhere.
2. **Entra-only SQL auth** (passwordless MI), same as DEV/TEST. Entra admin set, SQL logins
   disabled; each Function App MI gets a contained DB user.
3. **KSeF Function App stays PUBLIC** — BC (cloud) calls it directly; protect with its function
   key (optionally IP-restrict to BC). Only `func-auctionsystem-prod` goes behind the private
   endpoint.
4. **SWAs = Standard tier** (linked/private backend needs it). **Flex VNet-integration subnet
   needs a delegation** — verify the exact delegation string against current Azure docs at create
   time.

---

## Phase 0 — Facts (fill before Phase A)
| Item | Value |
|------|-------|
| PROD subscription id | _TBD_ |
| PROD tenant id | _TBD_ |
| Region | polandcentral (proposed) |
| SQL Entra admin (UPN / object-id) | _TBD_ |

**Proposed names** (clean convention):
| Resource | Name |
|---|---|
| Resource group | `rg-auctionsystem-prod` |
| Auction Function App | `func-auctionsystem-prod` |
| KSeF Function App | `func-ksef-prod` |
| Admin SWA | `swa-auction-prod` |
| Catalog SWA | `swa-auctioncatalog-prod` |
| SQL server / db | `sql-auctionsystem-prod` / `auctiondb` |
| VNet (10.20.0.0/16) | `vnet-auction-prod` — subnets `snet-pe`, `snet-runner`, `snet-func` |
| Key Vault | `kv-auction-prod` |
| Storage (auction func) | `stauctionprod…` (identity-based) |
| Storage (ksef func) | `stksefprod…` (identity-based) |

---

## Phase A — Core resources  *(you: az/portal in PROD tenant)*
- [ ] Resource group.
- [ ] VNet `10.20.0.0/16` + subnets: `snet-pe` (private endpoints), `snet-runner` (VM),
      `snet-func` (Flex VNet integration — **delegated**, verify string).
- [ ] **Azure SQL** server + `auctiondb`. Entra-only auth (`--enable-ad-only-auth`), Entra admin
      set. GP serverless (size per Phase E; PROD ~200 users).
- [ ] Storage account for auction func — public blob access off, TLS1.2.
- [ ] Storage account for KSeF func — same.
- [ ] **Auction Function App** (Flex Consumption, dotnet-isolated 8.0) — identity-based
      `AzureWebJobsStorage` + deployment storage; MI → Storage Blob Data Owner on its storage.
- [ ] **KSeF Function App** (Flex Consumption, dotnet-isolated 8.0) — same identity-storage wiring.
- [ ] **Admin SWA** (Standard) + **Catalog SWA** (Standard).
- [ ] Entra app reg — **CI/OIDC** (federated to this repo, access to the RG).
- [ ] Entra app reg — **SWA login** (redirect URIs = admin SWA hostname).
- [ ] Key Vault (SQL/BC/KSeF secrets land here, not in repo).

## GitHub secrets (`*_PROD`)  *(you)*
- [ ] `AZURE_CLIENT_ID_PROD`, `AZURE_TENANT_ID_PROD`, `AZURE_SUBSCRIPTION_ID_PROD`
- [ ] `AZURE_FUNCTIONAPP_NAME_PROD`, `AZURE_RESOURCE_GROUP_PROD`
- [ ] `AZURE_SWA_TOKEN_PROD`, `AZURE_SWA_TOKEN_CATALOG_PROD`, `API_BASE_URL_PROD`
- [ ] `KSEF_FUNCTIONAPP_NAME_PROD`, `KSEF_RESOURCE_GROUP_PROD`

## Phase B — Network lock  *(you)*
- [ ] Private endpoint on **auction** func (sub-resource `sites`) in `snet-pe`;
      `privatelink.azurewebsites.net` DNS zone linked to the VNet.
- [ ] Auction func → Networking → **Public network access = Disabled**.
- [ ] SWA → linked backend re-linked over the private endpoint (Standard private backend).
- [ ] **KSeF func stays public** (function key; optional BC IP restriction).
- [ ] Verify: direct `https://func-auctionsystem-prod.azurewebsites.net/api/brokers` unreachable;
      app via the SWA hostname works.

## Phase C — In-VNet VM runner  *(you, + me for workflow edits)*
- [ ] Small VM in `snet-runner`; install the GitHub Actions runner agent; label `prod-vnet`.
- [ ] **(me)** Rewire `deploy-prod.yml` — move **only the auction Functions deploy job** to
      `runs-on: [self-hosted, prod-vnet]` (the two SWA jobs + KSeF func deploy stay
      `ubuntu-latest`; they don't touch the private func app).

## First deploy  *(me, once A–C + secrets done)*
- [ ] Merge `test` → `prod`, push → `deploy-prod.yml` runs through the in-VNet runner.
- [ ] Watch all jobs green.

## Phase D — App security · DB seed · BC  *(both)*
- [ ] `AUTH_ENFORCE=true` on the auction func.
- [ ] SQL conn (MI) + BC creds in func config / Key Vault.
- [ ] Contained DB user for each func MI.
- [ ] **Lot-gen config seed** into PROD db (`LotSizeRule`, `LotGroupOrder`, `LotSortOrder`,
      `StringDefinition`, `CatalogNumberRule`) — `src/AuctionSystem.Functions/Sql/seed_lotgen_config.sql`.
- [ ] Verify Program.cs startup SQL built schema + composite indexes (Phase E1, already in code).
- [ ] Seed AppUsers with BrokerId/BuyerId/FarmerId so scoping doesn't 403 anyone.
- [ ] **BC PROD wiring:** company; API OAuth creds; register app reg as BC **Application User
      (Enabled)**; perms D365 BASIC + auctionSystem + CDC ADMIN; pin `BusinessCentral__CompanyId`
      GUID; publish the **3 posting-group page web services**
      (GenBusinessPostingGroups / VATBusinessPostingGroups / CustomerPostingGroups).

## KSeF  *(you)*
- [ ] KSeF func config + auth secret; `KSeF:BaseUrl` = **test URL** for now.
- [ ] BC: KSeF extension installed in the PROD company.
- [ ] When MF prod auth lands → set `KSeF:BaseUrl=ksef.mf.gov.pl` + load prod credential.

## Phase F — Verification
- [ ] Direct auction func URL → blocked.
- [ ] Anonymous SWA `/api/*` → 401.
- [ ] Broker / buyer / farmer / admin portals load and behave.
- [ ] Scoping: a broker can't read another broker's data by id → 403.
- [ ] BrokerLots / BuyerPurchases scoped to selected auction; auction switch works.
- [ ] CI deploys green through the in-VNet path.
- [ ] BC posting-group dropdowns populate; a test invoice pushes to BC.

---

## Status log
- _(start)_ — PROD greenfield; `prod` branch 89 commits behind `test`. Plan locked, provisioning not started.
