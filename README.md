# Auction System — Azure

Production-grade auction system built on Azure infrastructure.

## Architecture

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **Backend API** | Azure Functions (.NET 8, Isolated Worker) | REST API for auctions, lots, bids, settlements |
| **Frontend** | Blazor Server (.NET 8) | All portals: Dashboard, Auction, Broker, Buyer, Seller, Settlements |
| **Database** | Azure SQL Server | `auction` schema in shared database |
| **Domain** | .NET 8 Class Library | Shared entities, services, EF Core DbContext |

## Database Schema

All tables live in the `auction` schema to coexist with other applications:

- `auction.Auctions` — Auction events
- `auction.Lots` — Items for sale
- `auction.Sellers` — Consignors
- `auction.Brokers` — Intermediary buyers
- `auction.Buyers` — End customers (linked to brokers)
- `auction.Bids` — Bid records
- `auction.LotAllocations` — Broker-to-buyer allocations
- `auction.Invoices` / `auction.InvoiceLines` — Broker invoices
- `auction.Settlements` — Seller payouts

## Business Flow

1. Sellers consign goods → Lots created in backend
2. Backend creates and manages auctions
3. On-prem auction: Brokers bid and buy lots
4. Broker allocates purchased lots to their end customers
5. Invoices generated for brokers (subtotal + 10% commission + 25% tax)
6. Settlements created for sellers (gross - 10% commission - 2% fees = 88% net)

## Local Development

### Prerequisites
- .NET 8 SDK
- Azure Functions Core Tools v4 (for Functions project)

### Blazor Frontend
```bash
cd src/AuctionSystem.Web
# Set connection string via environment variable
export ConnectionStrings__DefaultConnection="Server=tcp:iftsqltest.database.windows.net,1433;Initial Catalog=PortalDBtest;User ID=saadmin;Password=YOUR_PASSWORD;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
dotnet run
```

### Azure Functions Backend
```bash
cd src/AuctionSystem.Functions
# Update local.settings.json with your SqlConnectionString
func start
```

### EF Core Migrations
```bash
# Add migration
dotnet ef migrations add MigrationName --project src/AuctionSystem.Domain --startup-project src/AuctionSystem.Web

# Apply migration
dotnet ef database update --project src/AuctionSystem.Domain --startup-project src/AuctionSystem.Web --connection "YOUR_CONNECTION_STRING"
```

## API Endpoints (Azure Functions)

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/auctions` | List all auctions |
| GET | `/api/auctions/{id}` | Get auction details |
| POST | `/api/auctions` | Create auction |
| PUT | `/api/auctions/{id}/status` | Update auction status |
| GET | `/api/auctions/{id}/lots` | Get lots for auction |
| POST | `/api/auctions/{id}/lots` | Add lot to auction |
| GET | `/api/lots/{id}` | Get lot details |
| POST | `/api/lots/{id}/hammer` | Record hammer price |
| POST | `/api/bids` | Place bid |
| GET | `/api/bids/broker/{id}` | Get bids by broker |
| POST | `/api/bids/allocate` | Allocate lot to buyer |
| GET | `/api/brokers` | List brokers |
| GET | `/api/sellers` | List sellers |
| POST | `/api/settlements/invoices/generate` | Generate invoice |
| POST | `/api/settlements/create` | Create settlement |
| GET | `/api/dashboard` | Dashboard stats |
| POST | `/api/seed` | Seed database |

## Deployment

### Azure Web App (Blazor)
```bash
dotnet publish src/AuctionSystem.Web -c Release -o publish/web
# Deploy to Azure App Service
```

### Azure Functions
```bash
dotnet publish src/AuctionSystem.Functions -c Release -o publish/functions
# Deploy to Azure Function App
func azure functionapp publish <function-app-name>
```

Set the `SqlConnectionString` application setting in both Azure resources.
