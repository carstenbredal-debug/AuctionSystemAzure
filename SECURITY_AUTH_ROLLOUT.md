# Security rollout — close the public API exposure (SWA-linked-backend auth)

**Status:** STAGED on branch `security/swa-auth-rollout`. The backend is already linked (verified),
so deploying this branch is safe — the app will route through SWA. Then do the two Azure settings
(steps 2-3 below). Roll out in order and verify with the curl checks.

## Why
The Blazor WASM app was calling the Function App **directly** at its `azurewebsites.net` URL,
bypassing Static Web Apps. With no SWA-injected identity header, `AUTH_ENFORCE` had to be `false`,
leaving the API **open to the public internet** (verified: `GET /api/brokers`, `/api/buyers`,
`/api/settlements/invoices` all returned data with no auth).

The fix routes the browser through SWA (`/api/*` on the app's own origin). SWA validates the
logged-in user and injects a **trusted, un-forgeable** `x-ms-client-principal` header; the Function
App is no longer reachable directly.

## Confirmed live state (probed 2026-06-16)
- SWA `AuctionSystem` (Standard) at `https://icy-beach-06b561303.7.azurestaticapps.net` serves the app.
- The Function App **is already linked** as the SWA backend — `…azurestaticapps.net/api/brokers`
  proxies to it. So "link the backend" is already done.
- BUT `…/api/brokers` returned full data with **no login**, because (a) `staticwebapp.config.json`
  had **no rule protecting `/api/*`**, (b) `AUTH_ENFORCE=false` on the Function App, and (c) the
  Function App's own `*.azurewebsites.net` URL is reachable directly.

## The staged code changes (this branch)
1. `src/AuctionSystem.Web/wwwroot/staticwebapp.config.json`: added route rule
   `{ "route": "/api/*", "allowedRoles": ["authenticated"] }` — SWA now returns 401 for
   unauthenticated `/api` calls and injects a validated identity header for authenticated ones.
2. `src/AuctionSystem.Web/wwwroot/appsettings.json`: `ApiBaseUrl` set to `""` — the WASM app calls
   `/api/...` on its own SWA origin (through the proxy) instead of the Function App's public URL.

## Rollout order (do not reorder)
1. **Deploy this branch** (merge `security/swa-auth-rollout` → the branch the SWA deploys from).
   After this the app calls the API through SWA, and SWA blocks anonymous `/api` calls.
2. **Enforce auth.** Function App → Configuration / Environment variables → set `AUTH_ENFORCE=true`
   (or delete it; default enforces). Save (restarts the app).
3. **Lock the Function App's public access** so its `*.azurewebsites.net` URL can't be hit directly
   (Networking → public access → restrict / disable). Closes the bypass route.

## Verification (must all pass)
- Direct call is now **blocked**:
  `curl -i https://auctionsystemazure-…azurewebsites.net/api/brokers` → **401 / blocked**, NOT 200+data.
- Through the app (logged in): brokers/buyers/typists can use the site normally.
- A logged-out browser is sent to login, not to data.

## Rollback (if the app breaks after deploy)
Set `AUTH_ENFORCE=false` again **and** revert this branch's appsettings change (restore the
direct `ApiBaseUrl`). That returns to the previous (working but insecure) state while you debug —
do NOT leave it there.

## Still TODO afterward (defense-in-depth, not urgent once the above lands)
Even with real auth, several endpoints (`/api/settlements/invoices`, `/api/brokers`, `…/unpushed`)
return ALL data to any authenticated user — they must be scoped to the caller's own broker/buyer.
That is the separate "Priority 2" hardening sprint.
