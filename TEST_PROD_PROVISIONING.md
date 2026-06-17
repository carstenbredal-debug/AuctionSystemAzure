# TEST / PROD Provisioning & Hardening Runbook

One checklist to stand up a new environment (TEST first, then PROD) with security and
performance **built in from day one** — rather than retrofitting, the way DEV was.

- **Branch mapping:** `test` → TEST, `prod` → PROD (see `.github/workflows/deploy-test.yml`,
  `deploy-prod.yml`). `dev` → DEV already exists.
- **Companion docs:** app-level auth rollout is in [`SECURITY_AUTH_ROLLOUT.md`](SECURITY_AUTH_ROLLOUT.md).
  This runbook adds the network lock, the in-VNet deploy path, and the performance infra.
- **Why this exists:** DEV runs on app-level controls only and is deliberately left that way
  (no real PII in DEV). TEST/PROD must be network-isolated because the Functions auth middleware
  trusts the `x-ms-client-principal` header, which is only safe when the Function App is reachable
  **solely via the SWA linked backend**.

Do TEST end-to-end first, prove it, then repeat the identical steps for PROD.

---

## 0. Facts to gather before you start

| Item | TEST | PROD |
|------|------|------|
| Resource group (fresh, clean-named) | | |
| Region (same for SWA backend + Function App + SQL) | | |
| Function App name | | |
| Static Web App name + default hostname | | |
| Azure SQL server + database name | | |
| Entra app registration (client/tenant id) for the SWA login | | |

> Keep TEST and PROD in **separate resource groups and separate SQL servers**. No shared data.

---

## Phase A — Core resources

- [ ] Create the resource group (clean naming convention, unlike DEV).
- [ ] **Azure SQL**: server + database. Start TEST small (e.g. General Purpose serverless, auto-pause
      off); size PROD for ~200 concurrent users (see Phase E). Enable Entra admin.
- [ ] **Function App on Flex Consumption** (matches DEV; supports VNet + private endpoints, pay-per-use).
- [ ] **Static Web App (Standard)** — required for linked backends + private backends.
- [ ] **Entra app registration** for SWA authentication (redirect URIs for the SWA hostname).
- [ ] Wire the SWA → Function App **linked backend** (initially public; locked in Phase B).

---

## Phase B — Network lock (the core hardening)

Goal: the Function App is **not reachable from the internet**; only the SWA-injected, validated
`x-ms-client-principal` reaches it.

- [ ] Create a **VNet** (e.g. `10.x.0.0/16`) + a subnet for private endpoints.
- [ ] Add a **private endpoint** to the Function App (target sub-resource `sites`) in that subnet,
      with the `privatelink.azurewebsites.net` Private DNS zone linked to the VNet.
- [ ] Function App → **Networking → Public network access → Disabled**.
- [ ] SWA → **APIs / linked backend** → re-link over the private endpoint (SWA Standard private
      backend). *Verify this screen against current Azure docs — the SWA backend UI changes; do it
      interactively the first time.*
- [ ] **Verify:** the direct `https://<funcapp>.azurewebsites.net/api/brokers` is unreachable, and
      the app served through the SWA hostname still works.

---

## Phase C — Deploy pipeline inside the network

Disabling public access **breaks the existing CI** — `deploy-test.yml` / `deploy-prod.yml` push the
zip from a GitHub-hosted runner over the public endpoint
(`az functionapp deployment source config-zip`). Pick one path:

- [ ] **Self-hosted GitHub runner inside the VNet** (recommended; prod-correct). A small VM or
      container-app runner in the VNet; point the deploy job at it so the zip-push happens privately.
      Build it once for TEST, reuse the pattern for PROD.
- [ ] *Alternative:* deploy via the Flex deployment **storage container** (push the package to the
      app's deployment blob); needs the runner to reach that storage account (storage firewall +
      dynamic runner IPs make this fiddlier).

> This runner work is the bulk of the effort — budget for it.

---

## Phase D — App-level security carry-forward (already proven on DEV)

- [ ] `AUTH_ENFORCE=true` on the Function App.
- [ ] SWA `staticwebapp.config.json` keeps `{"route":"/api/*","allowedRoles":["authenticated"]}`.
- [ ] `wwwroot/appsettings.json` `ApiBaseUrl=""` (route API via the SWA origin, env-agnostic).
      `deploy-*.yml` already writes this at build time.
- [ ] Per-user **data scoping** is already in code (ownership guards + admin-locked endpoints +
      redaction) — environment-agnostic, nothing extra to configure. See `SECURITY_AUTH_ROLLOUT.md`.
- [ ] Store SQL + BC secrets in the Function App config / Key Vault (no secrets in the repo).
- [ ] Seed AppUsers with their entity ids (BrokerId/BuyerId/FarmerId) so scoping doesn't 403 anyone.

---

## Phase E — Performance infra (the P3 items)

### E1. Composite SQL indexes
The broker/buyer grids now filter by **(owner + auction)**. Add composites in
`AuctionSystem.Domain/Data/AuctionDbContext.cs` (AuctionResult config, ~line 191) — these supersede
the standalone `BrokerId` / `SoldToBuyerId` indexes:

```csharp
// replaces e.HasIndex(r => r.BrokerId) and e.HasIndex(r => r.SoldToBuyerId)
e.HasIndex(r => new { r.BrokerId, r.AuctionId });
e.HasIndex(r => new { r.SoldToBuyerId, r.AuctionId });
e.HasIndex(r => r.LotNumber);
```

- [ ] `dotnet ef migrations add AddAuctionResultCompositeIndexes`
- [ ] Apply on deploy (migration step / `database update`). Safe to apply to DEV too once validated.

### E2. Blazor WASM startup
- [ ] Confirm SWA serves **Brotli/gzip** for `_framework/*` (Standard does this automatically — verify
      response `content-encoding`). This is most of the download win.
- [ ] **Lazy-load** heavy admin-only assemblies/routes (`BlazorWebAssemblyLazyLoad`) so broker/buyer
      sessions don't download admin code.
- [ ] *Optional, test carefully:* `<PublishTrimmed>true</PublishTrimmed>` (watch for reflection/JSON
      trimming breakage) and `<RunAOTCompilation>true</RunAOTCompilation>` (faster execution, larger
      download — usually not worth it for this app). Validate the whole app if enabled.

### E3. SQL sizing & connection health
- [ ] Size PROD for ~200 concurrent users (General Purpose / serverless with a sensible max vCore, or
      Business Critical if latency-sensitive). TEST can stay small.
- [ ] Verify EF connection pooling is effective under Flex Consumption scale-out (watch for pool
      exhaustion / transient SQL errors at load; add retry-on-failure if needed).
- [ ] **Measure once a representative auction is loaded** (DEV has none yet). The sizing queries:
      ```sql
      SELECT TOP 10 BrokerId, AuctionId, COUNT(*) AS Lots
      FROM AuctionResults GROUP BY BrokerId, AuctionId ORDER BY Lots DESC;     -- per-auction grid size
      SELECT TOP 10 BuyerId,  COUNT(*) FROM Invoices GROUP BY BuyerId  ORDER BY 2 DESC;
      SELECT TOP 10 BrokerId, COUNT(*) FROM Invoices GROUP BY BrokerId ORDER BY 2 DESC;
      ```
      If any single broker exceeds a few hundred lots in one auction, add `<Virtualize>` to that grid;
      if invoice counts are large, page the financial pages (currently un-paged, low volume).

---

## Phase F — Verification (run per environment)

- [ ] Direct Function App URL → **blocked** (no public route).
- [ ] Anonymous request to the SWA `/api/*` → **401**.
- [ ] Logged-in broker, buyer, farmer, admin → their portals load and behave normally.
- [ ] Scoping spot-check: a broker cannot read another broker's data by id (expect 403).
- [ ] BrokerLots / BuyerPurchases load scoped to the selected auction; auction switch works.
- [ ] BrokerCustomers buyer **search** returns capped results and excludes linked/requested.
- [ ] CI deploys successfully through the in-VNet path (Phase C).

---

## Open decisions to settle per environment
- Self-hosted runner host: VM vs container app (cost vs simplicity).
- SQL tier per environment (and whether PROD uses serverless autoscale or provisioned).
- Whether to enable WASM trimming/AOT (only after a full regression pass).
