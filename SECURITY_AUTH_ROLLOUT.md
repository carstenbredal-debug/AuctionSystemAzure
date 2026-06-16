# Security rollout — close the public API exposure (SWA-linked-backend auth)

**Status:** STAGED on branch `security/swa-auth-rollout`. Do NOT merge to `dev` until the
Azure steps below are done in order — deploying the config change before the Function App is
linked as the SWA backend will 404 every API call and take the app down.

## Why
The Blazor WASM app was calling the Function App **directly** at its `azurewebsites.net` URL,
bypassing Static Web Apps. With no SWA-injected identity header, `AUTH_ENFORCE` had to be `false`,
leaving the API **open to the public internet** (verified: `GET /api/brokers`, `/api/buyers`,
`/api/settlements/invoices` all returned data with no auth).

The fix routes the browser through SWA (`/api/*` on the app's own origin). SWA validates the
logged-in user and injects a **trusted, un-forgeable** `x-ms-client-principal` header; the Function
App is no longer reachable directly.

## The staged code change (this branch)
`src/AuctionSystem.Web/wwwroot/appsettings.json`: `ApiBaseUrl` set to `""`.
With it empty, `Program.cs` falls back to the app's own origin, so API calls go to `/api/...`
through the SWA proxy instead of the Function App's public URL.

## Rollout order (do not reorder)
1. **Link the backend.** Azure Portal → Static Web App → **APIs** → link the Function App as the
   backend. Requires SWA **Standard** tier. (If already linked, confirm it points at this Function App.)
2. **Deploy this branch** (merge `security/swa-auth-rollout` → `dev`, or your normal deploy).
   The WASM app now calls `/api/*` on the SWA origin.
3. **Lock the Function App.** Disable/restrict its public network access so the
   `*.azurewebsites.net` URL cannot be hit directly (only the SWA linked backend reaches it).
4. **Enforce auth.** Function App → Configuration → set `AUTH_ENFORCE=true` (or remove it; default
   enforces). Save (restarts the app).

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
