---
name: testing-auction-azure-bc
description: Test the AuctionSystemAzure BC integration end-to-end. Use when verifying broker/buyer/farmer creation, BC sync, invoicing, or entity management changes.
---

# Testing the AuctionSystemAzure BC Integration

## Prerequisites

- Access to the deployed Azure Functions API
- BC OAuth2 credentials (client ID, client secret, tenant ID)
- BC sandbox environment with a target company

## Devin Secrets Needed

- `BC_CLIENT_SECRET` — OAuth2 client secret for BC API access (client credentials flow)

## Environment

- **Functions API Base**: `https://auctionsystemazure-hjdgbugnfvfsaka7.westeurope-01.azurewebsites.net/api`
- **BC Tenant ID**: `a39a6ad1-89d2-4586-9383-4b19c32c2110`
- **BC Client ID**: `d9602d41-14cc-4d7e-bb4a-a634761c77a0`
- **BC Environment**: `sandbox`
- **BC Company**: "Lot Test 3" (`a410e9ea-9b54-f111-a820-7c1e5271a821`)
- **Custom BC APIs**:
  - Vendors (page 50101): `api/auctionSystem/integration/v1.0/companies({COMPANY_ID})/vendors`
  - Customers (page 50100): `api/auctionSystem/integration/v1.0/companies({COMPANY_ID})/customers`

## Entity Mappings

- **Broker → BC Vendor** (matched by BrokerNumber)
- **Farmer → BC Vendor** (matched by FarmerNumber)
- **Buyer → BC Customer** (matched by BuyerNumber)

## How to Get a BC Token

```bash
BC_TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id={CLIENT_ID}" \
  -d "client_secret=$BC_CLIENT_SECRET" \
  -d "scope=https://api.businesscentral.dynamics.com/.default" \
  -d "grant_type=client_credentials" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")
```

**Important:** BC tokens expire quickly. Refresh the token before each BC API call to avoid 401 errors.

## Testing Entity Creation + BC Sync

### 1. Create via API

```bash
# Broker
curl -s -X POST "{API_BASE}/brokers" -H "Content-Type: application/json" -d '{"brokerNumber":"100","companyName":"Test Broker","addressLine1":"Street 1","city":"City","postalCode":"1000","country":"DK","contactEmail":"test@test.dk","currency":"EUR","genBusPostingGroup":"EU","vatBusPostingGroup":"EU","paymentTerm":"PROMPT","creditLimit":50000,"isActive":true}'

# Buyer
curl -s -X POST "{API_BASE}/brokers/0/buyers/add" -H "Content-Type: application/json" -d '{"buyerNumber":"200","name":"Test Buyer","addressLine1":"Street 2","city":"City","postalCode":"2000","country":"DE","contactEmail":"buyer@test.de","currency":"EUR","genBusPostingGroup":"EU","vatBusPostingGroup":"EU","customerPostingGroup":"EU","paymentTerm":"PROMPT","creditLimit":100000,"vatRegistrationNo":"DE123456789","isActive":true}'

# Farmer
curl -s -X POST "{API_BASE}/farmers" -H "Content-Type: application/json" -d '{"farmerNumber":"300","name":"Test Farm","addressLine1":"Farm Rd","city":"Town","postalCode":"3000","country":"DK","contactEmail":"farm@test.dk","currency":"EUR","paymentTerm":"PROMPT","isActive":true}'
```

### 2. Verify in BC

```bash
# Check vendors (brokers + farmers)
curl -s -H "Authorization: Bearer $BC_TOKEN" "https://api.businesscentral.dynamics.com/v2.0/{TENANT_ID}/{ENV}/api/auctionSystem/integration/v1.0/companies({COMPANY_ID})/vendors"

# Check customers (buyers)
curl -s -H "Authorization: Bearer $BC_TOKEN" "https://api.businesscentral.dynamics.com/v2.0/{TENANT_ID}/{ENV}/api/auctionSystem/integration/v1.0/companies({COMPANY_ID})/customers"
```

### 3. Manual Sync (if auto-sync didn't fire)

```bash
curl -s -X POST "{API_BASE}/bc/sync/brokers"
curl -s -X POST "{API_BASE}/bc/sync/buyers"
curl -s -X POST "{API_BASE}/bc/sync/farmers"
```

## Available BC Reference Data

Query these to populate dropdowns or validate field values:

- **Gen. Bus. Posting Groups**: EU, NONEU, POLAND
- **VAT Bus. Posting Groups**: EU, NONEU, POLAND
- **Customer Posting Groups**: EU, NONEU
- **Vendor Posting Groups**: (none configured in sandbox — might change)
- **Payment Terms**: PROMPT
- **Countries**: 48 available via `GET {API_BASE}/bc/countries`

## Known Issues & Workarounds

- **Custom BC API pages reject unknown fields**: Pages 50100 (customer) and 50101 (vendor) expose a subset of fields. Fields like `taxLiable`, `state`, `type` must be excluded from POST requests via `[JsonIgnore]`. If you get a 400 error from BC, check which fields the custom page exposes vs what the model serializes.

- **SWA frontend might return 404**: The Azure Static Web Apps Build and Deploy CI job can be skipped, leaving the frontend un-deployed. In this case, test via API calls instead of browser UI.

- **Farmer→Vendor mapping is incomplete**: As of PR #88, `MapFarmerToVendor()` only maps basic fields (number, name, address, contact, currency). It does NOT map `paymentTermsCode`, posting groups, `blocked`, or `creditLimit`. The broker→vendor mapping maps all of these.

- **BC token expiry**: Tokens expire quickly. Always refresh before each BC API call sequence.

- **Reset all data**: `POST {API_BASE}/system/reset-all` wipes all entities except SystemParameters. Useful for clean-slate testing.

## Tips

- All Functions API endpoints are anonymous (no auth required for testing).
- Buyer creation endpoint is `POST /api/brokers/0/buyers/add` (broker ID 0 means unlinked).
- The `blocked` field uses BC enum values: empty string or `_x0020_` = not blocked, `Ship`, `Invoice`, `All`.
- When verifying BC sync, check both the count AND specific field values (displayName, posting groups, address) to ensure the mapping is correct.
- To test the full BC sync pipeline, create an entity → verify in Auction System → verify in BC via custom API.
