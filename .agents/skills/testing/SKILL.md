---
name: testing-auction-azure
description: Test the AuctionSystemAzure app end-to-end. Use when verifying admin portal, broker portal, buyer portal, invoicing, shipping, or BC integration changes.
---

# Testing AuctionSystemAzure

## Deployed Application

- **URL**: https://auctionsystemazure-hjdgbugnfvfsaka7.westeurope-01.azurewebsites.net
- **API Base**: https://auctionsystemazure-hjdgbugnfvfsaka7.westeurope-01.azurewebsites.net/api
- **Auth**: Azure AD (internal for admins) + External ID (for broker/buyer partners)

## Key API Endpoints

### Settlements / Invoices
- `GET /api/settlements/invoices` — All invoices with credit analysis
- `PUT /api/settlements/invoices/{id}/status` — Update invoice status
- `POST /api/settlements/invoices/{id}/downpayment` — Process downpayment with shipping release
- `GET /api/shipping/boxes` — Get lots released for shipping

### Auction Results
- `POST /api/auction-results/sell` — Sell lots from broker to buyer
- `POST /api/auction-results/takeback` — Takeback lots (creates credit note)
- `POST /api/auction-results/reassign-unsold` — Reassign unsold lots

### Entities
- `GET /api/brokers` — All brokers
- `GET /api/buyers` — All buyers
- `GET /api/farmers` — All farmers
- `GET /api/auctions` — All auctions
- `GET /api/catalog/lots` — Catalog lots

### Business Central
- `POST /api/bc/sync/invoices` — Push invoices to BC
- `POST /api/bc/sync/credit-notes` — Push credit notes to BC
- `GET /api/bc/consistency-check` — Run BC consistency checks

### Skins
- `GET /api/skins/sold?page=1&pageSize=100` — Sold skins (paginated)

### Diagnostics
- `GET /api/diag/schema/{tableName}` — Get DB table schema
- `GET /api/skins/price-check` — Run skins price integrity check

## Admin Portal Pages

| Page | Route | What to test |
|------|-------|--------------|
| Invoices | /admin/invoices | Filters, downpayment popup, credit status, Show Lots |
| Shipping | /admin/shipping | Shows lots released for shipping |
| Broker Detail | /admin/broker/{id} | Tabs: Info, Customers, Lots (Purchased/Sold/Unsold/Takeback) |
| Buyer Detail | /admin/buyer/{id} | Tabs: Info, Broker Relations, Purchases, Financial |
| Farmer Detail | /admin/farmer/{id} | Tabs: Info, Lots |
| Auction Simulation | /admin/auction-simulation | Sell lots, commission popup |
| Skins | /admin/skins | Sold skins table (paginated) |
| Data Integrity | /admin/data-integrity | Price check diagnostics |
| Business Central | /admin/business-central | Sync status, push buttons, consistency check |

## Key Test Flows

### 1. Sell Lots → Invoice Generation
1. Go to Auction Simulation
2. Select lots, set broker and buyer
3. Click Sell → invoice is auto-generated and pushed to BC
4. Check Invoices page shows new invoice

### 2. Downpayment + Shipping Release
1. Go to Invoices page
2. Click "Downpayment" on an invoice
3. Enter amount (% or absolute)
4. Confirm → "Release lots for shipping?" → Yes
5. Verify invoice shows "DP Paid" with "Shipped" badge
6. Go to Shipping page → verify lots appear

### 3. Takeback → Credit Note
1. From broker lots, select sold lots
2. Click Takeback → credit note auto-generated
3. Check Invoices page → original invoice shows "Partially/Fully Credited"
4. Click "Show Lots" → see credited vs uncredited lots

### 4. BC Sync
1. Push invoices via admin BC page
2. Push credit notes
3. Run consistency check
4. Verify PDF links work

## Database

- **Azure SQL** with two DbContexts:
  - `AuctionDbContext` (schema: `auction`) — main entities
  - `CatalogDbContext` (schema: `dbo`) — catalog lots, skins
- Boxes view: `dbo.boxes` (BoxNumber, BoxType, SalesType, Group, Gender, Size, HairLength, Color, Quality, Clarity, Damages, Skins)
- Skins table: `dbo.skintable`
- Cannot connect directly from Devin VM (IP not whitelisted) — use deployed API endpoints or diagnostic schema endpoint

## Notes

- Invoice status enum: Draft, Issued, Sent, Downpayment, Paid, Overdue, Cancelled
- ShippingStatus on invoice: null or "Released"
- Credit analysis: compare invoice lots against credit note lots to determine Fully/Partially Credited
- Internal broker (number 999) used for reassigning unsold lots
