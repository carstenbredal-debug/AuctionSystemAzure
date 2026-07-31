---
name: development-auction-azure
description: Development guide for AuctionSystemAzure. Use when building features, fixing bugs, or understanding the codebase structure.
---

# Development Guide — AuctionSystemAzure

## Project Structure

```
AuctionSystem.sln
src/
  AuctionSystem.Domain/       # Entities, DbContexts, Enums, Services
  AuctionSystem.Functions/    # Azure Functions API (HTTP triggers)
  AuctionSystem.Web/          # Blazor WASM frontend (Static Web App)
```

## Build & Run

```bash
# Build entire solution
dotnet build

# Build with restore
dotnet build --restore

# Run Functions locally (requires Azure Functions Core Tools)
cd src/AuctionSystem.Functions && func start

# Run Web locally
cd src/AuctionSystem.Web && dotnet run
```

## Deployment

- **Frontend**: Azure Static Web Apps (auto-deploys on push to `base-init`)
- **Backend**: Azure Functions (deployed via GitHub Actions workflow `base-init_auctionsystemazure.yml`)
- **CI**: Both workflows trigger on push to `base-init` and on PRs

## Architecture

### Backend (Azure Functions)

- HTTP-triggered functions in `src/AuctionSystem.Functions/Functions/`
- Each file groups related endpoints (e.g., `SettlementFunctions.cs` has all invoice/settlement routes)
- Pattern: `[Function("Name")] + [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "...")]`
- DI via constructor injection of `AuctionDbContext`, `CatalogDbContext`, services
- JSON serialization with `System.Text.Json` (camelCase by default)

### Frontend (Blazor WASM)

- Pages in `src/AuctionSystem.Web/Pages/` (Admin/, Broker/, Buyer/)
- DTOs in `src/AuctionSystem.Web/Models/Models.cs`
- API client: `src/AuctionSystem.Web/Services/AuctionApiClient.cs`
- Auth: Azure AD for admin, External ID for partners
- NavMenu: `src/AuctionSystem.Web/Layout/NavMenu.razor`

### Domain

- Entities in `src/AuctionSystem.Domain/Entities/`
- Two DbContexts:
  - `AuctionDbContext` → schema `auction` (main entities: Invoices, Lots, Brokers, Buyers, etc.)
  - `CatalogDbContext` → schema `dbo` (CatalogLots, Skins)
- Enums in `src/AuctionSystem.Domain/Enums/`

## Database Migration Pattern

There is **no EF Core migration Designer file**, so migrations are done via raw SQL in `Program.cs` startup:

```csharp
// In Program.cs after host.Build()
db.Database.ExecuteSqlRaw(@"
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.TableName') AND name = 'NewColumn')
        ALTER TABLE auction.TableName ADD NewColumn nvarchar(50) NULL;
");
```

Always use `IF NOT EXISTS` guard for idempotency.

## Adding a New Feature (Checklist)

1. **Entity** — Add/modify in `src/AuctionSystem.Domain/Entities/`
2. **Migration** — Add raw SQL in `Program.cs` startup block
3. **API Endpoint** — Add function in appropriate `*Functions.cs` file
4. **DTO** — Add to `src/AuctionSystem.Web/Models/Models.cs`
5. **API Client** — Add method to `AuctionApiClient.cs`
6. **UI Page** — Create/modify `.razor` file in appropriate portal
7. **Nav Menu** — Update `NavMenu.razor` if new page
8. **Build** — Run `dotnet build` to verify
9. **PR** — Create PR against `base-init`

## Key Patterns

### Invoice/Credit Note Pattern
- Invoice created on lot sell (auto-pushed to BC)
- Credit note created on takeback (`IsCreditNote = true`, `OriginalInvoiceId` set)
- PDF fetched from BC after posting, stored in Azure Blob Storage
- Credit analysis: compare lot numbers between invoice and its credit notes

### Status Update Pattern
```csharp
// Simple status endpoint
[Function("UpdateSomethingStatus")]
public async Task<HttpResponseData> UpdateStatus(
    [HttpTrigger(..., Route = "resource/{id:int}/status")] HttpRequestData req, int id)
{
    var body = await req.ReadFromJsonAsync<StatusRequest>();
    var entity = await _db.Entities.FirstOrDefaultAsync(e => e.Id == id);
    entity.Status = body.Status;
    await _db.SaveChangesAsync();
    return await CreateJsonResponse(req, new { entity.Id, entity.Status });
}
```

### Frontend Modal Pattern (Blazor)
```razor
@if (_showModal)
{
    <div class="modal d-block" tabindex="-1" style="background-color: rgba(0,0,0,0.5);">
        <div class="modal-dialog">
            <div class="modal-content">...</div>
        </div>
    </div>
}
```

## Business Central Integration

- BC client: `src/AuctionSystem.Functions/BusinessCentral/`
- OAuth2 client credentials flow
- Endpoints: Custom API pages for vendors/customers
- Standard API for sales invoices and credit memos
- Key: exclude read-only fields on POST, handle `@odata.etag` on PATCH
- Items used: LOTSALE, AUCTFEE, BROKERCOMM (service items)

## Environment Variables (Secrets)

- `SqlConnectionString` — Azure SQL connection
- `AzureWebJobsStorage` — Storage account for blobs
- `BC_TENANT_ID`, `BC_CLIENT_ID`, `BC_CLIENT_SECRET` — BC OAuth
- `BC_ENVIRONMENT`, `BC_COMPANY_ID`, `BC_COMPANY_NAME` — BC target

## Common Issues

- **JSON deserialization errors**: Frontend DTO must match API response exactly (string vs enum)
- **BC 400 errors**: Usually read-only fields being sent on POST — exclude them
- **DB column missing**: Add migration SQL to `Program.cs` with IF NOT EXISTS guard
- **Blazor rendering issues**: Complex expressions in `@(...)` need proper parenthesization
- **IP not whitelisted**: Can't connect to Azure SQL directly — use deployed endpoints
