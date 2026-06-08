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
    .ConfigureFunctionsWebApplication()
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
        });

        var bcTenantId = context.Configuration["BC_TENANT_ID"]
            ?? context.Configuration["Values:BC_TENANT_ID"] ?? "";
        if (!string.IsNullOrEmpty(bcTenantId))
        {
            services.AddSingleton<BusinessCentralAuthService>();
            services.AddHttpClient<BusinessCentralApiClient>();
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
                    InvoiceId INT NOT NULL,
                    BoxNumber INT NULL,
                    Notes NVARCHAR(500) NOT NULL DEFAULT '',
                    CONSTRAINT FK_ShipmentLines_Shipment FOREIGN KEY (ShipmentId) REFERENCES auction.Shipments(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_ShipmentLines_Invoice FOREIGN KEY (InvoiceId) REFERENCES auction.Invoices(Id)
                );
                CREATE INDEX IX_ShipmentLines_ShipmentId ON auction.ShipmentLines(ShipmentId);
                CREATE INDEX IX_ShipmentLines_InvoiceId ON auction.ShipmentLines(InvoiceId);
            END
        ");
        // Drop auction.Boxes table if it exists (replaced by view)
        db.Database.ExecuteSqlRaw(@"
            IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND name = 'Boxes')
                DROP TABLE auction.Boxes;
        ");
        // auction.Boxes view (reads from dbo.SkinTable)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.views WHERE schema_id = SCHEMA_ID('auction') AND name = 'Boxes')
                EXEC('CREATE VIEW auction.Boxes AS
                    SELECT
                        s.BoxNumber, s.BoxType, s.SalesType, s.[Group], s.Gender,
                        s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages,
                        COUNT(*) AS Skins
                    FROM dbo.SkinTable s
                    WHERE s.BoxStatus IN (''Showlot'', ''Storage'') AND s.IsActive = 1
                    GROUP BY s.BoxNumber, s.BoxType, s.SalesType, s.[Group], s.Gender,
                        s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages');
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
