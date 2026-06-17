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

        var storageConnectionString = context.Configuration["AzureWebJobsStorage"]
            ?? context.Configuration["Values:AzureWebJobsStorage"];
        if (!string.IsNullOrEmpty(storageConnectionString) && storageConnectionString != "UseDevelopmentStorage=true")
        {
            services.AddSingleton(sp => new BlobStorageService(
                storageConnectionString,
                sp.GetRequiredService<ILogger<BlobStorageService>>()));
        }

        // Background BC push queue (enqueue side). No-ops if storage is unconfigured.
        services.AddSingleton(sp => new AuctionSystem.Functions.BusinessCentral.Services.BcPushQueue(
            storageConnectionString,
            sp.GetRequiredService<ILogger<AuctionSystem.Functions.BusinessCentral.Services.BcPushQueue>>()));
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
        // TypistEntries: add AuctionId column
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.TypistEntries') AND name = 'AuctionId')
                ALTER TABLE auction.TypistEntries ADD AuctionId INT NOT NULL DEFAULT 0;
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
            IF NOT EXISTS (SELECT 1 FROM auction.SystemParameters WHERE [Key] = 'BcItem_Commission')
                INSERT INTO auction.SystemParameters ([Key], Value, Description, DataType, UpdatedAt)
                VALUES ('BcItem_Commission', 'BROKERCOMM', 'BC item number for the commission line', 'string', GETUTCDATE());
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
