using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Services;
using AuctionSystem.Functions.Functions;
using AuctionSystem.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(worker =>
    {
        // Outermost: turn unhandled exceptions into a 500 carrying the root-cause message.
        worker.UseMiddleware<AuctionSystem.Functions.Auth.ExceptionHandlingMiddleware>();
        // Authenticate/authorize every HTTP function (default: authenticated; see attributes).
        worker.UseMiddleware<AuctionSystem.Functions.Auth.AuthenticationMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        var connectionString = context.Configuration["SqlConnectionString"]
            ?? context.Configuration["Values:SqlConnectionString"]
            ?? throw new InvalidOperationException("SqlConnectionString is not configured");

        var sqlConnectionString = ConfigureManagedIdentity(connectionString);

        services.AddDbContext<AuctionDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        var targetCatalogConnectionString = context.Configuration["TargetCatalogConnectionString"]
            ?? context.Configuration["Values:TargetCatalogConnectionString"]
            ?? context.Configuration["ConnectionStrings:TargetCatalogConnectionString"]
            ?? context.Configuration.GetConnectionString("TargetCatalogConnectionString")
            ?? "";
        var targetSqlConnectionString = string.IsNullOrEmpty(targetCatalogConnectionString)
            ? "" : ConfigureManagedIdentity(targetCatalogConnectionString);
        services.AddSingleton(new TargetCatalogDbOptions { ConnectionString = targetSqlConnectionString });

        services.AddScoped<AuctionService>();
        services.AddScoped<BidService>();
        services.AddScoped<SettlementService>();

        // Business Central integration
        services.Configure<BusinessCentralOptions>(opts =>
        {
            opts.TenantId = context.Configuration["BC_TENANT_ID"]
                ?? context.Configuration["Values:BC_TENANT_ID"] ?? "";
            opts.ClientId = context.Configuration["BC_CLIENT_ID"]
                ?? context.Configuration["Values:BC_CLIENT_ID"] ?? "";
            opts.ClientSecret = context.Configuration["BC_CLIENT_SECRET"]
                ?? context.Configuration["Values:BC_CLIENT_SECRET"] ?? "";
            opts.Environment = context.Configuration["BC_ENVIRONMENT"]
                ?? context.Configuration["Values:BC_ENVIRONMENT"] ?? "sandbox";
            opts.CompanyId = context.Configuration["BC_COMPANY_ID"]
                ?? context.Configuration["Values:BC_COMPANY_ID"] ?? "";
            opts.CompanyName = context.Configuration["BC_COMPANY_NAME"]
                ?? context.Configuration["Values:BC_COMPANY_NAME"] ?? "Lot Test 3";

            // Dimension systemIds — per-company. Default to the Lot Test 3 ids so existing config keeps
            // working; override per environment via these settings (read them with GET /api/bc/dimensions).
            Guid Dim(string key, string fallback) =>
                Guid.TryParse(context.Configuration[key] ?? context.Configuration[$"Values:{key}"], out var g) ? g : Guid.Parse(fallback);
            opts.VendorTypeDimensionId    = Dim("BC_DIM_VENDORTYPE_ID",               "0288ef73-a554-f111-a820-7c1e5271a821");
            opts.VendorTypeBrokerValueId  = Dim("BC_DIM_VENDORTYPE_BROKER_VALUE_ID",  "728c715d-be54-f111-a820-7c1e5271a821");
            opts.VendorTypeFarmerValueId  = Dim("BC_DIM_VENDORTYPE_FARMER_VALUE_ID",  "0688ef73-a554-f111-a820-7c1e5271a821");
            opts.CustomerTypeDimensionId  = Dim("BC_DIM_CUSTOMERTYPE_ID",             "0188ef73-a554-f111-a820-7c1e5271a821");
            opts.CustomerTypeBuyerValueId = Dim("BC_DIM_CUSTOMERTYPE_BUYER_VALUE_ID", "a807e197-f55a-f111-a820-70a8a55fc40b");
        });

        var bcTenantId = context.Configuration["BC_TENANT_ID"]
            ?? context.Configuration["Values:BC_TENANT_ID"] ?? "";
        if (!string.IsNullOrEmpty(bcTenantId))
        {
            services.AddSingleton<BusinessCentralAuthService>();
            services.AddTransient<AuctionSystem.Functions.BusinessCentral.Services.BcRetryHandler>();
            // Bound each logical BC call (incl. the retry loop, which sits inside this pipeline) so a
            // hung BC endpoint can't tie up a worker for the full 100s default or stack across retries.
            services.AddHttpClient<BusinessCentralApiClient>(c => c.Timeout = TimeSpan.FromSeconds(90))
                .AddHttpMessageHandler<AuctionSystem.Functions.BusinessCentral.Services.BcRetryHandler>();
            services.AddScoped<BusinessCentralSyncService>();
        }

        // Lot generation services
        services.AddScoped<LotGenerationService>();
        services.AddScoped<CatalogBuildService>();
        // Scanner completion flows resolve these to flip a fully-packed shipment to Ready and
        // regenerate its shipping documents.
        services.AddScoped<AuctionSystem.Functions.Functions.ShipmentFunctions>();
        services.AddScoped<ShipmentReadyService>();
        services.AddSingleton<EmailService>();

        // Storage clients: a connection string (local dev / key-based) OR the func's managed identity.
        // In Azure (PROD/TEST) we use identity-based AzureWebJobsStorage (`__accountName`, no key/connection
        // string), so build blob + queue clients with DefaultAzureCredential against the account endpoints.
        var storageConnectionString = context.Configuration["AzureWebJobsStorage"]
            ?? context.Configuration["Values:AzureWebJobsStorage"];
        var storageAccountName = context.Configuration["AzureWebJobsStorage:accountName"]
            ?? context.Configuration["AzureWebJobsStorage__accountName"]
            ?? context.Configuration["Values:AzureWebJobsStorage:accountName"];
        var hasStorageConn = !string.IsNullOrEmpty(storageConnectionString) && storageConnectionString != "UseDevelopmentStorage=true";

        Azure.Storage.Queues.QueueClient? MakeQueue(string name)
        {
            var opts = new Azure.Storage.Queues.QueueClientOptions { MessageEncoding = Azure.Storage.Queues.QueueMessageEncoding.Base64 };
            if (hasStorageConn)
                return new Azure.Storage.Queues.QueueClient(storageConnectionString, name, opts);
            if (!string.IsNullOrEmpty(storageAccountName))
                return new Azure.Storage.Queues.QueueClient(
                    new Uri($"https://{storageAccountName}.queue.core.windows.net/{name}"),
                    new Azure.Identity.DefaultAzureCredential(), opts);
            return null;
        }

        // Blob storage (invoice PDFs): connection string or managed identity.
        if (hasStorageConn)
        {
            services.AddSingleton(sp => new BlobStorageService(
                new Azure.Storage.Blobs.BlobServiceClient(storageConnectionString),
                sp.GetRequiredService<ILogger<BlobStorageService>>()));
        }
        else if (!string.IsNullOrEmpty(storageAccountName))
        {
            services.AddSingleton(sp => new BlobStorageService(
                new Azure.Storage.Blobs.BlobServiceClient(
                    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                    new Azure.Identity.DefaultAzureCredential()),
                sp.GetRequiredService<ILogger<BlobStorageService>>()));
        }

        // Background BC push queue (enqueue side). No-ops if storage is unconfigured.
        services.AddSingleton(sp => new AuctionSystem.Functions.BusinessCentral.Services.BcPushQueue(
            MakeQueue(AuctionSystem.Functions.BusinessCentral.Services.BcPushQueue.QueueName),
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.BusinessCentral.Services.BcPushQueue>>()));

        // Background snapshot-build queue (large auction imports). Synchronous fallback if unconfigured.
        services.AddSingleton(sp => new AuctionSystem.Functions.Services.SnapshotBuildQueue(
            MakeQueue(AuctionSystem.Functions.Services.SnapshotBuildQueue.QueueName),
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.Services.SnapshotBuildQueue>>()));

        // Background catalogue-import queue (2+ catalogues overrun the gateway timeout). Sync fallback.
        services.AddSingleton(sp => new AuctionSystem.Functions.Services.CatalogImportQueue(
            MakeQueue(AuctionSystem.Functions.Services.CatalogImportQueue.QueueName),
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.Services.CatalogImportQueue>>()));

        // Background catalogue-freeze (Activate) queue: a big catalogue's SELECT INTO overruns the gateway
        // timeout if done inline. Sync fallback when storage is unconfigured (local dev).
        services.AddSingleton(sp => new AuctionSystem.Functions.Services.CatalogFreezeQueue(
            MakeQueue(AuctionSystem.Functions.Services.CatalogFreezeQueue.QueueName),
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.Services.CatalogFreezeQueue>>()));

        // Background paced typist-simulator queue (runs over time at a configurable delay).
        services.AddSingleton(sp => new AuctionSystem.Functions.Services.TypistSimQueue(
            MakeQueue(AuctionSystem.Functions.Services.TypistSimQueue.QueueName),
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.Services.TypistSimQueue>>()));

        // Background paced broker-robot simulator queue (runs passes server-side until done/stopped).
        services.AddSingleton(sp => new AuctionSystem.Functions.Services.BrokerSimQueue(
            MakeQueue(AuctionSystem.Functions.Services.BrokerSimQueue.QueueName),
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.Services.BrokerSimQueue>>()));
    })
    .Build();

// Apply pending EF Core migrations on startup
using (var scope = host.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        // Rename Sellers table to Farmers if needed
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'auction' AND TABLE_NAME = 'Sellers')
                AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'auction' AND TABLE_NAME = 'Farmers')
            BEGIN
                EXEC sp_rename 'auction.Sellers', 'Farmers';
            END
            -- Rename FK columns on Lots table
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Lots') AND name = 'SellerId')
            BEGIN
                EXEC sp_rename 'auction.Lots.SellerId', 'FarmerId', 'COLUMN';
            END
            -- Rename columns on Farmers table
            IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'auction' AND TABLE_NAME = 'Farmers')
                AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'SellerNumber')
            BEGIN
                EXEC sp_rename 'auction.Farmers.SellerNumber', 'FarmerNumber', 'COLUMN';
            END
            -- Rename SellerId on AppUsers
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.AppUsers') AND name = 'SellerId')
            BEGIN
                EXEC sp_rename 'auction.AppUsers.SellerId', 'FarmerId', 'COLUMN';
            END
            -- Rename SellerId on Settlements
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Settlements') AND name = 'SellerId')
            BEGIN
                EXEC sp_rename 'auction.Settlements.SellerId', 'FarmerId', 'COLUMN';
            END
            -- Update AppRole values from 'Seller' to 'Farmer'
            IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'auction' AND TABLE_NAME = 'AppUsers')
                UPDATE auction.AppUsers SET Role = 'Farmer' WHERE Role = 'Seller';
        ");
        // Run EF Core migrations first to create base tables
        db.Database.Migrate();
        // Add columns not covered by EF migrations (no Designer file)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'CreditLimit')
                ALTER TABLE auction.Brokers ADD CreditLimit decimal(18,2) NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'Blocked')
                ALTER TABLE auction.Brokers ADD Blocked nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'GenBusPostingGroup')
                ALTER TABLE auction.Brokers ADD GenBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'VatBusPostingGroup')
                ALTER TABLE auction.Brokers ADD VatBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'CustomerPostingGroup')
                ALTER TABLE auction.Brokers ADD CustomerPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            -- Buyer new columns
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Buyers') AND name = 'CreditLimit')
                ALTER TABLE auction.Buyers ADD CreditLimit decimal(18,2) NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Buyers') AND name = 'Blocked')
                ALTER TABLE auction.Buyers ADD Blocked nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Buyers') AND name = 'GenBusPostingGroup')
                ALTER TABLE auction.Buyers ADD GenBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Buyers') AND name = 'VatBusPostingGroup')
                ALTER TABLE auction.Buyers ADD VatBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Buyers') AND name = 'CustomerPostingGroup')
                ALTER TABLE auction.Buyers ADD CustomerPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            -- Farmer new columns
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'CreditLimit')
                ALTER TABLE auction.Farmers ADD CreditLimit decimal(18,2) NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'Blocked')
                ALTER TABLE auction.Farmers ADD Blocked nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'GenBusPostingGroup')
                ALTER TABLE auction.Farmers ADD GenBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'VatBusPostingGroup')
                ALTER TABLE auction.Farmers ADD VatBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'VendorPostingGroup')
                ALTER TABLE auction.Farmers ADD VendorPostingGroup nvarchar(max) NOT NULL DEFAULT '';
        ");
        // BcSyncedAt: entity property (Broker/Buyer/Farmer) mapped by convention but missing from
        // the DB, which made every full-entity load/save fail with 'Invalid column name BcSyncedAt'.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'BcSyncedAt')
                ALTER TABLE auction.Brokers ADD BcSyncedAt datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Buyers') AND name = 'BcSyncedAt')
                ALTER TABLE auction.Buyers ADD BcSyncedAt datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Farmers') AND name = 'BcSyncedAt')
                ALTER TABLE auction.Farmers ADD BcSyncedAt datetime2 NULL;
        ");
        // Make Invoices.InvoiceNumber unique index FILTERED: invoices/credit notes are created with
        // InvoiceNumber = '' until BC assigns the real number, and a plain unique index allows only
        // one '' row — so concurrent invoicing collides on a duplicate-key error. Enforce uniqueness
        // only on real (non-empty) numbers. Idempotent (rebuilds only while the index is unfiltered).
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Invoices_InvoiceNumber'
                       AND object_id = OBJECT_ID('auction.Invoices') AND has_filter = 0)
            BEGIN
                DROP INDEX IX_Invoices_InvoiceNumber ON auction.Invoices;
                CREATE UNIQUE INDEX IX_Invoices_InvoiceNumber ON auction.Invoices(InvoiceNumber)
                    WHERE InvoiceNumber <> '';
            END
        ");
        // Invoice BC columns
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'BcInvoiceNumber')
                ALTER TABLE auction.Invoices ADD BcInvoiceNumber nvarchar(50) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'BcInvoiceId')
                ALTER TABLE auction.Invoices ADD BcInvoiceId uniqueidentifier NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'ShippingStatus')
                ALTER TABLE auction.Invoices ADD ShippingStatus nvarchar(50) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'DownpaymentAmount')
                ALTER TABLE auction.Invoices ADD DownpaymentAmount decimal(18,2) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'DownpaymentPercentage')
                ALTER TABLE auction.Invoices ADD DownpaymentPercentage decimal(18,4) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'BcPushStartedAt')
                ALTER TABLE auction.Invoices ADD BcPushStartedAt datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'BcSyncError')
                ALTER TABLE auction.Invoices ADD BcSyncError nvarchar(1000) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'BcSyncErrorAt')
                ALTER TABLE auction.Invoices ADD BcSyncErrorAt datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'VatAmount')
                ALTER TABLE auction.Invoices ADD VatAmount decimal(18,2) NOT NULL CONSTRAINT DF_Invoices_VatAmount DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Invoices') AND name = 'TotalAmountInclVat')
                ALTER TABLE auction.Invoices ADD TotalAmountInclVat decimal(18,2) NOT NULL CONSTRAINT DF_Invoices_TotalAmountInclVat DEFAULT 0;
        ");
        // (No VAT backfill: existing invoices keep VatAmount/TotalAmountInclVat = 0 until regenerated.
        // New invoices/credit notes compute these at creation from the buyer's VAT Bus. Posting Group.)
        // CatalogDrafts: ShowLotCount frozen on the row (the catalogue's lots live in per-catalogue
        // auction.[Cat_{id}.Lots] tables which can't be GROUP BY'd across all drafts for the list page).
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDrafts') AND name = 'ShowLotCount')
                ALTER TABLE auction.CatalogDrafts ADD ShowLotCount int NOT NULL CONSTRAINT DF_CatalogDrafts_ShowLotCount DEFAULT 0;
        ");
        // Backfill ShowLotCount for catalogues created before the column existed (their lots are still in the
        // legacy CatalogDraftLots). New catalogues set it at creation, so only still-zero rows are touched.
        db.Database.ExecuteSqlRaw(@"
            UPDATE d SET d.ShowLotCount = x.cnt
            FROM auction.CatalogDrafts d
            JOIN (SELECT DraftId, COUNT(*) AS cnt FROM auction.CatalogDraftLots WHERE IsShow = 'Yes' GROUP BY DraftId) x
              ON x.DraftId = d.Id
            WHERE d.ShowLotCount = 0;
        ");
        // Per-line charged auction fee — credit notes credit this stored amount instead of recomputing.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.InvoiceLines') AND name = 'AuctionFee')
                ALTER TABLE auction.InvoiceLines ADD AuctionFee decimal(18,2) NULL;
        ");
        // External-price staging: a POSTed external result lands here first and waits out a 5-minute debounce
        // window (each correction bumps UpdatedAt) before a timer applies it to the real sale. One pending row
        // per (auction, lot) via the filtered unique index.
        db.Database.ExecuteSqlRaw(@"
            IF OBJECT_ID('auction.ExternalPriceStaging', 'U') IS NULL
            CREATE TABLE auction.ExternalPriceStaging (
                Id          int IDENTITY(1,1) PRIMARY KEY,
                AuctionId   int NOT NULL,
                LotNumber   int NOT NULL,
                BrokerId    int NOT NULL,
                PriceEur    decimal(18,2) NOT NULL,
                ExternalRef nvarchar(100) NULL,
                ReceivedAt  datetime2 NOT NULL,
                UpdatedAt   datetime2 NOT NULL,
                Applied     bit NOT NULL CONSTRAINT DF_ExternalPriceStaging_Applied DEFAULT 0,
                AppliedAt   datetime2 NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ExternalPriceStaging_Pending' AND object_id = OBJECT_ID('auction.ExternalPriceStaging'))
                CREATE UNIQUE INDEX UX_ExternalPriceStaging_Pending ON auction.ExternalPriceStaging(AuctionId, LotNumber) WHERE Applied = 0;
        ");
        // Supporting index for the eligible-skin grouped count (catalog list + activate). Without it the
        // GROUP BY over a multi-million-row SkinTable full-scans and overruns the HTTP timeout. Isolated in
        // its own try/catch so a one-time index build hiccup can never abort the rest of the migration.
        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SkinTable_Eligible' AND object_id = OBJECT_ID('dbo.SkinTable'))
                    CREATE NONCLUSTERED INDEX IX_SkinTable_Eligible
                        ON dbo.SkinTable (IsActive, BoxStatus, SalesType, Gender, [Group]);
            ");
        }
        catch (Exception ixEx)
        {
            scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
                .LogError(ixEx, "IX_SkinTable_Eligible creation failed; continuing startup");
        }
        // Auction snapshot-build status columns (background import)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'SnapshotStatus')
                ALTER TABLE auction.Auctions ADD SnapshotStatus nvarchar(400) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'SnapshotBuiltAt')
                ALTER TABLE auction.Auctions ADD SnapshotBuiltAt datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'TypistSimStatus')
                ALTER TABLE auction.Auctions ADD TypistSimStatus nvarchar(400) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'TypistSimStopRequested')
                ALTER TABLE auction.Auctions ADD TypistSimStopRequested bit NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'BrokerSimStatus')
                ALTER TABLE auction.Auctions ADD BrokerSimStatus nvarchar(400) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'BrokerSimStopRequested')
                ALTER TABLE auction.Auctions ADD BrokerSimStopRequested bit NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Auctions') AND name = 'ImportedCatalogIds')
                ALTER TABLE auction.Auctions ADD ImportedCatalogIds nvarchar(max) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.AuctionResults') AND name = 'ExternalRef')
                ALTER TABLE auction.AuctionResults ADD ExternalRef nvarchar(100) NULL;
        ");
        // TypistEntries table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'TypistEntries')
            BEGIN
                CREATE TABLE auction.TypistEntries (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    LotNumber INT NOT NULL,
                    BrokerId INT NOT NULL,
                    PriceEur DECIMAL(18,2) NOT NULL,
                    TypistUserId INT NOT NULL,
                    TypistSlot INT NOT NULL,
                    EnteredAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    IsMatched BIT NOT NULL DEFAULT 0,
                    IsDisagreement BIT NOT NULL DEFAULT 0,
                    IsResolved BIT NOT NULL DEFAULT 0,
                    MatchedWithEntryId INT NULL,
                    AuctionResultId INT NULL,
                    CONSTRAINT FK_TypistEntries_Broker FOREIGN KEY (BrokerId) REFERENCES auction.Brokers(Id),
                    CONSTRAINT FK_TypistEntries_TypistUser FOREIGN KEY (TypistUserId) REFERENCES auction.AppUsers(Id),
                    CONSTRAINT FK_TypistEntries_MatchedWith FOREIGN KEY (MatchedWithEntryId) REFERENCES auction.TypistEntries(Id),
                    CONSTRAINT FK_TypistEntries_AuctionResult FOREIGN KEY (AuctionResultId) REFERENCES auction.AuctionResults(Id)
                );
                CREATE INDEX IX_TypistEntries_LotNumber_Slot ON auction.TypistEntries(LotNumber, TypistSlot);
                CREATE INDEX IX_TypistEntries_TypistUserId ON auction.TypistEntries(TypistUserId);
            END
        ");
        // Catalogue draft tables — a frozen copy of cataloglots filtered by SalesType/Gender/Group.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'CatalogDrafts')
                CREATE TABLE auction.CatalogDrafts (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(150) NOT NULL,
                    SalesType NVARCHAR(50) NULL,
                    Gender NVARCHAR(50) NULL,
                    [Group] NVARCHAR(50) NULL,
                    LotCount INT NOT NULL DEFAULT 0,
                    SkinCount INT NOT NULL DEFAULT 0,
                    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                );
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'CatalogDraftLots')
            BEGIN
                CREATE TABLE auction.CatalogDraftLots (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    DraftId INT NOT NULL,
                    StringNumber INT NOT NULL DEFAULT 0,
                    LotNumber INT NOT NULL DEFAULT 0,
                    CatalogSortOrder INT NOT NULL DEFAULT 0,
                    IsShow NVARCHAR(10) NOT NULL DEFAULT '',
                    SalesType NVARCHAR(50) NULL,
                    Gender NVARCHAR(50) NULL,
                    [Group] NVARCHAR(50) NULL,
                    HairLength NVARCHAR(50) NULL,
                    Size NVARCHAR(50) NULL,
                    Quality NVARCHAR(50) NULL,
                    Color NVARCHAR(50) NULL,
                    Clarity NVARCHAR(50) NULL,
                    Damages NVARCHAR(50) NULL,
                    IncludedBoxNumbers NVARCHAR(MAX) NULL,
                    BoxCount INT NOT NULL DEFAULT 0,
                    TotalSkins INT NOT NULL DEFAULT 0,
                    CONSTRAINT FK_CatalogDraftLots_Draft FOREIGN KEY (DraftId) REFERENCES auction.CatalogDrafts(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_CatalogDraftLots_DraftId ON auction.CatalogDraftLots(DraftId);
            END
        ");
        // Catalogue lifecycle status: Draft -> Active -> InAuction.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDrafts') AND name = 'Status')
                ALTER TABLE auction.CatalogDrafts ADD Status NVARCHAR(20) NOT NULL DEFAULT 'Draft';
        ");
        // Editable per-lot catalogue fields.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDraftLots') AND name = 'Description')
                ALTER TABLE auction.CatalogDraftLots ADD Description NVARCHAR(500) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDraftLots') AND name = 'Estimate')
                ALTER TABLE auction.CatalogDraftLots ADD Estimate NVARCHAR(100) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDraftLots') AND name = 'RedLimit')
                ALTER TABLE auction.CatalogDraftLots ADD RedLimit NVARCHAR(100) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDraftLots') AND name = 'Remarks')
                ALTER TABLE auction.CatalogDraftLots ADD Remarks NVARCHAR(500) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.CatalogDraftLots') AND name = 'RackPosition')
                ALTER TABLE auction.CatalogDraftLots ADD RackPosition NVARCHAR(20) NULL;
        ");
        // auction.Lots: catalogue sort order (sales order) so lots/transactions sort like the PDF.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Lots') AND name = 'CatalogSortOrder')
                ALTER TABLE auction.Lots ADD CatalogSortOrder INT NOT NULL DEFAULT 0;
        ");
        // TypistEntries: add AuctionId column
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.TypistEntries') AND name = 'AuctionId')
                ALTER TABLE auction.TypistEntries ADD AuctionId INT NOT NULL DEFAULT 0;
        ");
        // TypistEntries: filtered UNIQUE index so a lot can be typed into a slot only ONCE per auction.
        // Two concurrent / redelivered sim workers otherwise both typed the same lot (each picking its
        // own random winning broker) -> duplicate AuctionResults that double-count brokers and poison
        // every per-broker rollup. Filtered on IsResolved=0 so the disagreement-resolve path (mark the old
        // rows resolved, then insert fresh ones) still works. Dedup any pre-existing active duplicates
        // first (null their inbound MatchedWith links, then delete the extras) so the index can be built
        // even on a DB that already accumulated duplicates from the race.
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.TypistEntries') AND name = 'IsResolved')
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TypistEntries_Auction_Lot_Slot_Active')
            BEGIN
                UPDATE te SET te.MatchedWithEntryId = NULL
                FROM auction.TypistEntries te
                WHERE te.MatchedWithEntryId IN (
                    SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (PARTITION BY AuctionId, LotNumber, TypistSlot ORDER BY Id) rn
                                    FROM auction.TypistEntries WHERE IsResolved = 0) x WHERE x.rn > 1);

                DELETE FROM auction.TypistEntries WHERE Id IN (
                    SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (PARTITION BY AuctionId, LotNumber, TypistSlot ORDER BY Id) rn
                                    FROM auction.TypistEntries WHERE IsResolved = 0) x WHERE x.rn > 1);

                CREATE UNIQUE INDEX UX_TypistEntries_Auction_Lot_Slot_Active
                    ON auction.TypistEntries (AuctionId, LotNumber, TypistSlot) WHERE IsResolved = 0 AND IsDisagreement = 0;
            END
        ");

        // The active-uniqueness index must EXCLUDE disagreement rows. With the original filter (IsResolved=0
        // only), a disagreement re-entry inserts a fresh slot-1/2 row that collides (SQL 2601) with the
        // still-unresolved ORIGINAL disagreement row of the same (AuctionId, LotNumber, TypistSlot) — the
        // old rows aren't resolved until AFTER the colliding insert — so re-entry could never be submitted.
        // Re-filter to active, NON-disagreement rows: normal-typing/redelivered-worker duplicates
        // (IsDisagreement=0) stay protected, parked disagreement rows no longer block their own re-entry.
        // Idempotent: skips once the index already carries the IsDisagreement predicate. No dedup needed —
        // the old (superset) filter already guaranteed no active duplicates exist.
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.indexes
                       WHERE name = 'UX_TypistEntries_Auction_Lot_Slot_Active'
                         AND object_id = OBJECT_ID('auction.TypistEntries')
                         AND (filter_definition IS NULL OR filter_definition NOT LIKE '%IsDisagreement%'))
            BEGIN
                DROP INDEX UX_TypistEntries_Auction_Lot_Slot_Active ON auction.TypistEntries;
                CREATE UNIQUE INDEX UX_TypistEntries_Auction_Lot_Slot_Active
                    ON auction.TypistEntries (AuctionId, LotNumber, TypistSlot) WHERE IsResolved = 0 AND IsDisagreement = 0;
            END
        ");
        // Phase E1 (TEST/PROD perf): composite (owner + auction) indexes for the broker/buyer grids,
        // which now filter by both. They supersede the standalone BrokerId / SoldToBuyerId indexes (a
        // composite on (Col, AuctionId) also serves Col-only lookups via the leftmost-prefix rule), so
        // drop those once the composites exist. Idempotent; safe to re-run. Indexes go via startup SQL,
        // NOT EF migrations — the migration snapshot is frozen at the 2026-05-28 baseline.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuctionResults_BrokerId_AuctionId' AND object_id = OBJECT_ID('auction.AuctionResults'))
                CREATE INDEX IX_AuctionResults_BrokerId_AuctionId ON auction.AuctionResults(BrokerId, AuctionId);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuctionResults_SoldToBuyerId_AuctionId' AND object_id = OBJECT_ID('auction.AuctionResults'))
                CREATE INDEX IX_AuctionResults_SoldToBuyerId_AuctionId ON auction.AuctionResults(SoldToBuyerId, AuctionId);
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuctionResults_BrokerId' AND object_id = OBJECT_ID('auction.AuctionResults'))
               AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuctionResults_BrokerId_AuctionId' AND object_id = OBJECT_ID('auction.AuctionResults'))
                DROP INDEX IX_AuctionResults_BrokerId ON auction.AuctionResults;
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuctionResults_SoldToBuyerId' AND object_id = OBJECT_ID('auction.AuctionResults'))
               AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuctionResults_SoldToBuyerId_AuctionId' AND object_id = OBJECT_ID('auction.AuctionResults'))
                DROP INDEX IX_AuctionResults_SoldToBuyerId ON auction.AuctionResults;
        ");
        // AuctionTransactions table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'AuctionTransactions')
            BEGIN
                CREATE TABLE auction.AuctionTransactions (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    AuctionId INT NOT NULL,
                    LotNumber INT NOT NULL,
                    TransactionType INT NOT NULL,
                    BrokerId INT NOT NULL,
                    BuyerId INT NULL,
                    Description NVARCHAR(500) NOT NULL DEFAULT '',
                    Quantity INT NOT NULL DEFAULT 0,
                    UnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
                    Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
                    DebitAccount NVARCHAR(50) NULL,
                    CreditAccount NVARCHAR(50) NULL,
                    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    AuctionResultId INT NULL,
                    CONSTRAINT FK_AuctionTransactions_Auction FOREIGN KEY (AuctionId) REFERENCES auction.Auctions(Id),
                    CONSTRAINT FK_AuctionTransactions_Broker FOREIGN KEY (BrokerId) REFERENCES auction.Brokers(Id),
                    CONSTRAINT FK_AuctionTransactions_Buyer FOREIGN KEY (BuyerId) REFERENCES auction.Buyers(Id),
                    CONSTRAINT FK_AuctionTransactions_AuctionResult FOREIGN KEY (AuctionResultId) REFERENCES auction.AuctionResults(Id)
                );
                CREATE INDEX IX_AuctionTransactions_AuctionId_LotNumber ON auction.AuctionTransactions(AuctionId, LotNumber);
                CREATE INDEX IX_AuctionTransactions_TransactionType ON auction.AuctionTransactions(TransactionType);
                CREATE INDEX IX_AuctionTransactions_BrokerId ON auction.AuctionTransactions(BrokerId);
            END
        ");
        // BrokerCustomerRequests: add InitiatedBy column
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.BrokerCustomerRequests') AND name = 'InitiatedBy')
                ALTER TABLE auction.BrokerCustomerRequests ADD InitiatedBy nvarchar(10) NOT NULL DEFAULT 'Broker';
        ");
        // AuctionResults: add LastModifiedBy and LastModifiedAt columns
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.AuctionResults') AND name = 'LastModifiedBy')
                ALTER TABLE auction.AuctionResults ADD LastModifiedBy nvarchar(10) NULL;
        ");
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.AuctionResults') AND name = 'LastModifiedAt')
                ALTER TABLE auction.AuctionResults ADD LastModifiedAt datetime2 NULL;
        ");
        // LotSalesHistories table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'LotSalesHistories')
            BEGIN
                CREATE TABLE auction.LotSalesHistories (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    LotNumber INT NOT NULL,
                    AuctionResultId INT NOT NULL,
                    ActionType NVARCHAR(50) NOT NULL,
                    Initials NVARCHAR(10) NULL,
                    BuyerId INT NULL,
                    BuyerName NVARCHAR(200) NULL,
                    InvoiceId INT NULL,
                    InvoiceNumber NVARCHAR(100) NULL,
                    Amount DECIMAL(18,2) NULL,
                    Notes NVARCHAR(500) NULL,
                    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT FK_LotSalesHistories_AuctionResult FOREIGN KEY (AuctionResultId) REFERENCES auction.AuctionResults(Id),
                    CONSTRAINT FK_LotSalesHistories_Buyer FOREIGN KEY (BuyerId) REFERENCES auction.Buyers(Id),
                    CONSTRAINT FK_LotSalesHistories_Invoice FOREIGN KEY (InvoiceId) REFERENCES auction.Invoices(Id)
                );
                CREATE INDEX IX_LotSalesHistories_LotNumber ON auction.LotSalesHistories(LotNumber);
                CREATE INDEX IX_LotSalesHistories_AuctionResultId ON auction.LotSalesHistories(AuctionResultId);
            END
        ");
        // BoxTypeDimensions table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'BoxTypeDimensions')
            BEGIN
                CREATE TABLE auction.BoxTypeDimensions (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    BoxType NVARCHAR(100) NOT NULL,
                    HeightM DECIMAL(10,4) NOT NULL DEFAULT 0,
                    WidthM DECIMAL(10,4) NOT NULL DEFAULT 0,
                    LengthM DECIMAL(10,4) NOT NULL DEFAULT 0,
                    WeightKg DECIMAL(10,4) NOT NULL DEFAULT 0,
                    UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                );
                CREATE UNIQUE INDEX IX_BoxTypeDimensions_BoxType ON auction.BoxTypeDimensions(BoxType);
            END
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.BoxTypeDimensions') AND name = 'WeightKg')
                ALTER TABLE auction.BoxTypeDimensions ADD WeightKg DECIMAL(10,4) NOT NULL DEFAULT 0;
        ");
        // ShippingAddresses table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'ShippingAddresses')
            BEGIN
                CREATE TABLE auction.ShippingAddresses (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    BuyerId INT NOT NULL,
                    ContactName NVARCHAR(200) NOT NULL DEFAULT '',
                    AddressLine1 NVARCHAR(200) NOT NULL DEFAULT '',
                    AddressLine2 NVARCHAR(200) NOT NULL DEFAULT '',
                    Country NVARCHAR(100) NOT NULL DEFAULT '',
                    PostalCode NVARCHAR(20) NOT NULL DEFAULT '',
                    City NVARCHAR(100) NOT NULL DEFAULT '',
                    ContactPhone NVARCHAR(50) NOT NULL DEFAULT '',
                    MobilePhone NVARCHAR(50) NOT NULL DEFAULT '',
                    ContactEmail NVARCHAR(200) NOT NULL DEFAULT '',
                    IsDefault BIT NOT NULL DEFAULT 0,
                    IsActive BIT NOT NULL DEFAULT 1,
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    FOREIGN KEY (BuyerId) REFERENCES auction.Buyers(Id)
                );
                CREATE INDEX IX_ShippingAddresses_BuyerId ON auction.ShippingAddresses(BuyerId);
            END
        ");
        // Shippers table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'Shippers')
            BEGIN
                CREATE TABLE auction.Shippers (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(200) NOT NULL DEFAULT '',
                    Code NVARCHAR(50) NOT NULL DEFAULT '',
                    ContactName NVARCHAR(200) NOT NULL DEFAULT '',
                    Phone NVARCHAR(50) NOT NULL DEFAULT '',
                    Email NVARCHAR(200) NOT NULL DEFAULT '',
                    AddressLine1 NVARCHAR(200) NOT NULL DEFAULT '',
                    AddressLine2 NVARCHAR(200) NOT NULL DEFAULT '',
                    City NVARCHAR(100) NOT NULL DEFAULT '',
                    PostalCode NVARCHAR(20) NOT NULL DEFAULT '',
                    Country NVARCHAR(100) NOT NULL DEFAULT '',
                    Website NVARCHAR(500) NOT NULL DEFAULT '',
                    TrackingUrlTemplate NVARCHAR(500) NOT NULL DEFAULT '',
                    IsActive BIT NOT NULL DEFAULT 1,
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                );
                CREATE UNIQUE INDEX IX_Shippers_Code ON auction.Shippers(Code);
            END
        ");
        // Shipments table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'Shipments')
            BEGIN
                CREATE TABLE auction.Shipments (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    ShipmentNumber NVARCHAR(50) NOT NULL DEFAULT '',
                    ShipperId INT NOT NULL,
                    BuyerId INT NOT NULL,
                    ShippingAddressId INT NULL,
                    TrackingNumber NVARCHAR(200) NOT NULL DEFAULT '',
                    Status NVARCHAR(50) NOT NULL DEFAULT 'Pending',
                    Notes NVARCHAR(1000) NOT NULL DEFAULT '',
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    ShippedAt DATETIME2 NULL,
                    DeliveredAt DATETIME2 NULL,
                    CONSTRAINT FK_Shipments_Shipper FOREIGN KEY (ShipperId) REFERENCES auction.Shippers(Id),
                    CONSTRAINT FK_Shipments_Buyer FOREIGN KEY (BuyerId) REFERENCES auction.Buyers(Id),
                    CONSTRAINT FK_Shipments_ShippingAddress FOREIGN KEY (ShippingAddressId) REFERENCES auction.ShippingAddresses(Id)
                );
                CREATE UNIQUE INDEX IX_Shipments_ShipmentNumber ON auction.Shipments(ShipmentNumber);
                CREATE INDEX IX_Shipments_BuyerId ON auction.Shipments(BuyerId);
                CREATE INDEX IX_Shipments_ShipperId ON auction.Shipments(ShipperId);
                CREATE INDEX IX_Shipments_Status ON auction.Shipments(Status);
            END
        ");
        // ShipmentLines table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'ShipmentLines')
            BEGIN
                CREATE TABLE auction.ShipmentLines (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    ShipmentId INT NOT NULL,
                    LotNumber INT NOT NULL,
                    InvoiceId INT NULL,
                    Notes NVARCHAR(500) NOT NULL DEFAULT '',
                    CONSTRAINT FK_ShipmentLines_Shipment FOREIGN KEY (ShipmentId) REFERENCES auction.Shipments(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_ShipmentLines_Invoice FOREIGN KEY (InvoiceId) REFERENCES auction.Invoices(Id)
                );
                CREATE INDEX IX_ShipmentLines_ShipmentId ON auction.ShipmentLines(ShipmentId);
                CREATE INDEX IX_ShipmentLines_InvoiceId ON auction.ShipmentLines(InvoiceId);
                CREATE INDEX IX_ShipmentLines_LotNumber ON auction.ShipmentLines(LotNumber);
            END
        ");
        // Migrate ShipmentLines: add LotNumber column, make InvoiceId nullable, drop BoxNumber
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'ShipmentLines')
                AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.ShipmentLines') AND name = 'LotNumber')
            BEGIN
                ALTER TABLE auction.ShipmentLines ADD LotNumber INT NOT NULL DEFAULT 0;
                CREATE INDEX IX_ShipmentLines_LotNumber ON auction.ShipmentLines(LotNumber);
            END
        ");
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.ShipmentLines') AND name = 'InvoiceId' AND is_nullable = 0)
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ShipmentLines_Invoice')
                    ALTER TABLE auction.ShipmentLines DROP CONSTRAINT FK_ShipmentLines_Invoice;
                ALTER TABLE auction.ShipmentLines ALTER COLUMN InvoiceId INT NULL;
                ALTER TABLE auction.ShipmentLines ADD CONSTRAINT FK_ShipmentLines_Invoice FOREIGN KEY (InvoiceId) REFERENCES auction.Invoices(Id);
            END
        ");
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.ShipmentLines') AND name = 'BoxNumber')
                ALTER TABLE auction.ShipmentLines DROP COLUMN BoxNumber;
        ");
        // PackingOrders table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'PackingOrders')
            CREATE TABLE auction.PackingOrders (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                PackingOrderNumber NVARCHAR(50) NOT NULL DEFAULT '',
                ShipmentId INT NOT NULL,
                Status NVARCHAR(50) NOT NULL DEFAULT 'Ready to Pack',
                CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                CONSTRAINT FK_PackingOrders_Shipment FOREIGN KEY (ShipmentId) REFERENCES auction.Shipments(Id) ON DELETE CASCADE
            );
        ");
        // PackingOrderLines table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'PackingOrderLines')
            CREATE TABLE auction.PackingOrderLines (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                PackingOrderId INT NOT NULL,
                BoxNumber INT NOT NULL,
                LotNumber INT NOT NULL,
                Skins INT NOT NULL DEFAULT 0,
                BoxType NVARCHAR(100) NOT NULL DEFAULT '',
                CONSTRAINT FK_PackingOrderLines_PackingOrder FOREIGN KEY (PackingOrderId) REFERENCES auction.PackingOrders(Id) ON DELETE CASCADE
            );
        ");
        // PackedBoxes table
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'PackedBoxes')
            CREATE TABLE auction.PackedBoxes (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                PackingOrderId INT NOT NULL,
                BoxType NVARCHAR(100) NOT NULL DEFAULT '',
                Weight DECIMAL(18,4) NOT NULL DEFAULT 0,
                HeightM DECIMAL(18,4) NOT NULL DEFAULT 0,
                WidthM DECIMAL(18,4) NOT NULL DEFAULT 0,
                LengthM DECIMAL(18,4) NOT NULL DEFAULT 0,
                Status NVARCHAR(50) NOT NULL DEFAULT 'Packed',
                CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                CONSTRAINT FK_PackedBoxes_PackingOrder FOREIGN KEY (PackingOrderId) REFERENCES auction.PackingOrders(Id) ON DELETE CASCADE
            );
        ");
        // Add PackedBoxId column to PackingOrderLines if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackingOrderLines') AND name = 'PackedBoxId')
                ALTER TABLE auction.PackingOrderLines ADD PackedBoxId INT NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PackingOrderLines_PackedBox')
                ALTER TABLE auction.PackingOrderLines ADD CONSTRAINT FK_PackingOrderLines_PackedBox
                    FOREIGN KEY (PackedBoxId) REFERENCES auction.PackedBoxes(Id) ON DELETE NO ACTION;
        ");
        // Add Type column to PackingOrders if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackingOrders') AND name = 'Type')
                ALTER TABLE auction.PackingOrders ADD Type NVARCHAR(50) NOT NULL DEFAULT 'ShowLot';
        ");
        // Add Location column to PackingOrderLines if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackingOrderLines') AND name = 'Location')
                ALTER TABLE auction.PackingOrderLines ADD Location NVARCHAR(100) NOT NULL DEFAULT '';
        ");
        // Add new columns to PackedBoxes if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackedBoxes') AND name = 'BoxNumber')
                ALTER TABLE auction.PackedBoxes ADD BoxNumber NVARCHAR(50) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackedBoxes') AND name = 'GrossWeight')
                ALTER TABLE auction.PackedBoxes ADD GrossWeight DECIMAL(18,4) NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackedBoxes') AND name = 'NetWeight')
                ALTER TABLE auction.PackedBoxes ADD NetWeight DECIMAL(18,4) NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackedBoxes') AND name = 'TareWeight')
                ALTER TABLE auction.PackedBoxes ADD TareWeight DECIMAL(18,4) NOT NULL DEFAULT 0;
        ");
        // Add WeightKg column to PackingOrderLines if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackingOrderLines') AND name = 'WeightKg')
                ALTER TABLE auction.PackingOrderLines ADD WeightKg DECIMAL(18,4) NOT NULL DEFAULT 0;
        ");
        // Add default box tare weights to SystemParameters if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'BoxTareWeight_Big')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('BoxTareWeight_Big', '2.5', 'Tare weight (kg) for Big box type', 'decimal', GETUTCDATE());
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'BoxTareWeight_Small')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('BoxTareWeight_Small', '1.5', 'Tare weight (kg) for Small box type', 'decimal', GETUTCDATE());
        ");
        // BC item numbers used on invoice/credit-memo lines — admin-editable in the Parameters page.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'BcItem_LotSale')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('BcItem_LotSale', 'LOTSALE', 'BC item number for lot sale invoice/credit lines', 'string', GETUTCDATE());
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'BcItem_AuctionFee')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('BcItem_AuctionFee', 'AUCTFEE', 'BC item number for the auction fee line', 'string', GETUTCDATE());
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'ShippingDocsEmail')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('ShippingDocsEmail', '', 'Email address that receives the packing list + shipping invoice when a shipment is marked Shipped (empty = no email)', 'string', GETUTCDATE());
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'BcItem_Commission')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('BcItem_Commission', 'BROKERCOM', 'BC item number for the commission line', 'string', GETUTCDATE());
        ");
        // Add PackingListPdfUrl column to Shipments if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Shipments') AND name = 'PackingListPdfUrl')
                ALTER TABLE auction.Shipments ADD PackingListPdfUrl NVARCHAR(MAX) NULL;
        ");
        // Add ShippingInvoicePdfUrl column to Shipments if missing
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Shipments') AND name = 'ShippingInvoicePdfUrl')
                ALTER TABLE auction.Shipments ADD ShippingInvoicePdfUrl NVARCHAR(MAX) NULL;
        ");
        // Add CertUrl column to Shipments if missing (uploaded certificate document)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Shipments') AND name = 'CertUrl')
                ALTER TABLE auction.Shipments ADD CertUrl NVARCHAR(MAX) NULL;
        ");
        // Add OutLocation column to Shipments if missing (outgoing staging location OUT-1..OUT-20)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Shipments') AND name = 'OutLocation')
                ALTER TABLE auction.Shipments ADD OutLocation NVARCHAR(20) NULL;
        ");
        // Add MovedToOutAt column to PackingOrderLines if missing (scanner confirms the box was
        // moved from storage to the shipment's OUT location)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackingOrderLines') AND name = 'MovedToOutAt')
                ALTER TABLE auction.PackingOrderLines ADD MovedToOutAt DATETIME2 NULL;
        ");
        // Add LoadedAt columns (scanner confirms boxes/cartons loaded onto the courier's truck)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackingOrderLines') AND name = 'LoadedAt')
                ALTER TABLE auction.PackingOrderLines ADD LoadedAt DATETIME2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.PackedBoxes') AND name = 'LoadedAt')
                ALTER TABLE auction.PackedBoxes ADD LoadedAt DATETIME2 NULL;
        ");
        // BoxPhysicalState: physical staging/packing state per box, survives shipment deletion
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'BoxPhysicalState')
            CREATE TABLE auction.BoxPhysicalState (
                BoxNumber INT NOT NULL PRIMARY KEY,
                OutLocation NVARCHAR(20) NULL,
                MovedAt DATETIME2 NULL,
                PackedBoxNumber NVARCHAR(50) NULL,
                PackedBoxType NVARCHAR(100) NULL,
                PackedGrossWeight DECIMAL(18,4) NULL,
                UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
        ");
        // Drop auction.Boxes table if it exists (replaced by view)
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'Boxes')
                DROP TABLE auction.Boxes;
        ");
        // auction.Boxes view (reads from dbo.SkinTable) — recreate to include BoxStatus
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.views WHERE schema_id = SCHEMA_ID('auction') AND name = 'Boxes')
                DROP VIEW auction.Boxes;
        ");
        db.Database.ExecuteSqlRaw(@"
            EXEC('CREATE VIEW auction.Boxes AS
                SELECT
                    s.BoxNumber, s.BoxType, s.BoxStatus, s.SalesType, s.[Group], s.Gender,
                    s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages,
                    COUNT(*) AS Skins
                FROM dbo.SkinTable s
                WHERE s.BoxStatus IN (''Showlot'', ''Storage'') AND s.IsActive = 1
                GROUP BY s.BoxNumber, s.BoxType, s.BoxStatus, s.SalesType, s.[Group], s.Gender,
                    s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages');
        ");
        // Add AuctionId to AuctionResults
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.AuctionResults') AND name = 'AuctionId')
                ALTER TABLE auction.AuctionResults ADD AuctionId INT NOT NULL DEFAULT 0;
        ");
        // Backfill AuctionId on existing results where it's 0
        db.Database.ExecuteSqlRaw(@"
            UPDATE ar SET ar.AuctionId = (
                SELECT TOP 1 l.AuctionId FROM auction.Lots l WHERE l.LotNumber = ar.LotNumber AND l.AuctionId > 0
            )
            FROM auction.AuctionResults ar
            WHERE ar.AuctionId = 0
            AND EXISTS (SELECT 1 FROM auction.Lots l WHERE l.LotNumber = ar.LotNumber AND l.AuctionId > 0);
        ");
        // Migrate ShippingAddresses: add Name column, make BuyerId nullable (disconnect from buyer)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.ShippingAddresses') AND name = 'Name')
                ALTER TABLE auction.ShippingAddresses ADD Name NVARCHAR(200) NOT NULL DEFAULT '';
            -- Make BuyerId nullable
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.ShippingAddresses') AND name = 'BuyerId' AND is_nullable = 0)
            BEGIN
                -- Drop FK constraint if exists
                DECLARE @fkName NVARCHAR(200);
                SELECT @fkName = fk.name FROM sys.foreign_keys fk
                    JOIN sys.tables t ON fk.parent_object_id = t.object_id
                    WHERE t.name = 'ShippingAddresses' AND t.schema_id = SCHEMA_ID('auction');
                IF @fkName IS NOT NULL
                    EXEC('ALTER TABLE auction.ShippingAddresses DROP CONSTRAINT ' + @fkName);
                -- Drop index if exists
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('auction.ShippingAddresses') AND name = 'IX_ShippingAddresses_BuyerId')
                    DROP INDEX IX_ShippingAddresses_BuyerId ON auction.ShippingAddresses;
                -- Make column nullable
                ALTER TABLE auction.ShippingAddresses ALTER COLUMN BuyerId INT NULL;
            END
        ");
        // Add BoxWeight column to existing snapshot boxes tables
        db.Database.ExecuteSqlRaw(@"
            DECLARE @tbl NVARCHAR(200);
            DECLARE tbl_cursor CURSOR FOR
                SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name LIKE '%.Boxes';
            OPEN tbl_cursor;
            FETCH NEXT FROM tbl_cursor INTO @tbl;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.[' + @tbl + ']') AND name = 'BoxWeight')
                    EXEC('ALTER TABLE auction.[' + @tbl + '] ADD BoxWeight DECIMAL(18,2) NOT NULL DEFAULT 0');
                FETCH NEXT FROM tbl_cursor INTO @tbl;
            END
            CLOSE tbl_cursor;
            DEALLOCATE tbl_cursor;
        ");
        // Try to populate BoxWeight from staging table for existing snapshot boxes
        db.Database.ExecuteSqlRaw(@"
            DECLARE @tbl2 NVARCHAR(200);
            DECLARE tbl2_cursor CURSOR FOR
                SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name LIKE '%.Boxes';
            OPEN tbl2_cursor;
            FETCH NEXT FROM tbl2_cursor INTO @tbl2;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXEC('UPDATE t SET t.BoxWeight = ISNULL(b.Weight, 0) FROM auction.[' + @tbl2 + '] t LEFT JOIN dbo.boxstatingfromkphg b ON b.BoxNumber = t.BoxNumber WHERE t.BoxWeight = 0');
                FETCH NEXT FROM tbl2_cursor INTO @tbl2;
            END
            CLOSE tbl2_cursor;
            DEALLOCATE tbl2_cursor;
        ");
        db.Database.Migrate();
        // Ensure CatalogDbContext tables exist (CatalogLots, GeneratedLots, etc.)
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalogDb.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to apply database migrations on startup");
    }
}

host.Run();

static string ConfigureManagedIdentity(string connectionString)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrEmpty(builder.Password) &&
        string.IsNullOrEmpty(builder.UserID) &&
        !connectionString.Contains("Authentication=", StringComparison.OrdinalIgnoreCase) &&
        !connectionString.Contains("Integrated Security=", StringComparison.OrdinalIgnoreCase))
    {
        builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
    }
    return builder.ConnectionString;
}
