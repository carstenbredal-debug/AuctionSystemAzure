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

## Phase 0 — Facts (PROVISIONED)
| Item | Value |
|------|-------|
| PROD subscription id | `6161e948-e1d3-4c77-b77b-ab35491b2948` |
| PROD tenant id | `982dbb71-b602-4c81-a7a7-d2244f22bc65` |
| Region | polandcentral (SWAs in westeurope — SWA not in PL Central) |
| SQL Entra admin | set at create (`--enable-ad-only-auth`, verified `azureAdOnlyAuthentication=true`) |

**Actual resource names** (created):
| Resource | Name |
|---|---|
| Resource group | `Auction_Production` |
| Auction Function App | `func-auctionsystem-prod` (Flex FC1, plan `ASP-AuctionProduction-a351`) |
| KSeF Function App | `func-ksef-prod-86679` (Flex FC1) — reused/rebuilt in this RG |
| Admin SWA | `swa-auction-prod` → `lemon-grass-026391103.7.azurestaticapps.net` |
| Catalog SWA | `swa-auctioncatalog-prod` → `calm-river-0efbdc903.7.azurestaticapps.net` |
| SQL server / db | `sql-auctionsystem-prod` / `auctiondb` (GP serverless, 4 vCore, no auto-pause) |
| VNet (10.20.0.0/16) | `vnet-auction-prod` — `snet-pe`, `snet-runner`, `snet-func` (delegated `Microsoft.App/environments`) |
| Key Vault | `kv-auction-prod` (RBAC) |
| Storage (auction func) | `stauctionprod48422` (identity-based runtime storage) |
| Storage (ksef func) | `stksefprod26226` (identity-based runtime storage) |
| CI/OIDC app reg | `github-oidc-auction-prod` (federated to `prod` branch, Contributor on RG) |

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

## Phase B — Network lock + hardening  *(you, + me for decisions)*
- [ ] **Deployment-storage → identity** on BOTH funcs. Created with key-based
      `DEPLOYMENT_STORAGE_CONNECTION_STRING` (the other half of the TEST key-rotation outage).
      `--deployment-storage-auth-type SystemAssignedIdentity` is a create-time flag → one clean
      recreate of each (no code deployed yet). Runtime `AzureWebJobsStorage` already identity ✅.
- [ ] **DECISION — catalog-web reachability under the lock.** Public catalog SWA reaches the
      auction func cross-origin today; that breaks when public access is disabled. Options: (a)
      catalog SWA own private linked backend; (b) Front Door/App Gateway WAF over the private func;
      (c) separate public catalog API over the VNet. Settle before disabling public access.
      Sets `API_BASE_URL_PROD`.
- [ ] Private endpoint on **auction** func (sub-resource `sites`) in `snet-pe`;
      `privatelink.azurewebsites.net` DNS zone linked to the VNet.
- [ ] Auction func → Networking → **Public network access = Disabled**.
- [ ] Admin SWA → linked backend re-linked over the private endpoint (Standard private backend).
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
- **Phase A DONE** — all core resources provisioned in `Auction_Production` (see Phase 0 table), both
  funcs on Flex FC1 with identity-based runtime storage, SQL Entra-only verified, CI/OIDC app reg +
  federated `prod`-branch credential created, all 9 `*_PROD` GitHub secrets set (`API_BASE_URL_PROD`
  deferred to the Phase B catalog decision). Open: deployment-storage→identity (both funcs) +
  catalog-web reachability, both folded into Phase B before first deploy.
