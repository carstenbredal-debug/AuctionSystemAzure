using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class AuctionFunctions
{
    private readonly AuctionService _service;
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly ILogger<AuctionFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuctionFunctions(AuctionService service, AuctionDbContext db, CatalogDbContext catalogDb, ILogger<AuctionFunctions> logger)
    {
        _service = service;
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
    }

    [Function("GetAuctions")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auctions")] HttpRequestData req)
    {
        var auctions = await _service.GetAllAuctionsAsync();
        return await CreateJsonResponse(req, auctions);
    }

    [Function("GetAuction")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auctions/{id:int}")] HttpRequestData req, int id)
    {
        var auction = await _service.GetAuctionAsync(id);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, auction);
    }

    [Function("CreateAuction")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auctions")] HttpRequestData req)
    {
        var auction = await req.ReadFromJsonAsync<Auction>();
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var created = await _service.CreateAuctionAsync(auction);
        return await CreateJsonResponse(req, created, System.Net.HttpStatusCode.Created);
    }

    [Function("UpdateAuctionStatus")]
    public async Task<HttpResponseData> UpdateStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "auctions/{id:int}/status")] HttpRequestData req, int id)
    {
        var status = await req.ReadFromJsonAsync<AuctionStatus>();
        var auction = await _service.UpdateStatusAsync(id, status);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, auction);
    }

    [Function("GetLotsByAuction")]
    public async Task<HttpResponseData> GetLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auctions/{auctionId:int}/lots")] HttpRequestData req, int auctionId)
    {
        var lots = await _service.GetLotsByAuctionAsync(auctionId);
        var result = lots.Select(l => new
        {
            l.Id, l.LotNumber, l.Description, l.Category, l.Quantity, l.Unit,
            l.StartingPrice, l.HammerPrice, status = (int)l.Status, l.AuctionId
        });
        return await CreateJsonResponse(req, result);
    }

    [Function("AddLot")]
    public async Task<HttpResponseData> AddLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auctions/{auctionId:int}/lots")] HttpRequestData req, int auctionId)
    {
        var lot = await req.ReadFromJsonAsync<Lot>();
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var created = await _service.AddLotAsync(auctionId, lot);
        return await CreateJsonResponse(req, created, System.Net.HttpStatusCode.Created);
    }

    [Function("GetLot")]
    public async Task<HttpResponseData> GetLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lots/{id:int}")] HttpRequestData req, int id)
    {
        var lot = await _service.GetLotAsync(id);
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, lot);
    }

    [Function("RecordHammerPrice")]
    public async Task<HttpResponseData> RecordHammer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "lots/{lotId:int}/hammer")] HttpRequestData req, int lotId)
    {
        var request = await req.ReadFromJsonAsync<HammerRequest>();
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var lot = await _service.RecordHammerPriceAsync(lotId, request.HammerPrice, request.WinningBrokerId);
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, lot);
    }

    [Function("SeedDatabase")]
    public async Task<HttpResponseData> Seed(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "seed")] HttpRequestData req)
    {
        SeedData.Initialize(_db);
        return await CreateJsonResponse(req, new { message = "Database seeded successfully" });
    }

    [Function("GetDashboardStats")]
    public async Task<HttpResponseData> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequestData req)
    {
        var recentAuctions = await _db.Auctions.OrderByDescending(a => a.ScheduledDate).Take(5).ToListAsync();
        var stats = new
        {
            totalAuctions = await _db.Auctions.CountAsync(),
            activeAuctions = await _db.Auctions.CountAsync(a => a.Status == AuctionStatus.Active),
            totalLots = await _db.Lots.CountAsync(),
            soldLots = await _db.Lots.CountAsync(l => l.Status == LotStatus.Sold),
            totalBrokers = await _db.Brokers.CountAsync(),
            totalFarmers = await _db.Farmers.CountAsync(),
            totalBuyers = await _db.Buyers.CountAsync(),
            totalBids = await _db.Bids.CountAsync(),
            pendingInvoices = await _db.Invoices.CountAsync(i => i.Status == InvoiceStatus.Issued),
            pendingSettlements = await _db.Settlements.CountAsync(s => s.Status == SettlementStatus.Pending),
            recentAuctions
        };
        return await CreateJsonResponse(req, stats);
    }

    [Function("ImportLotsToAuction")]
    public async Task<HttpResponseData> ImportLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auctions/{auctionId:int}/import-lots")] HttpRequestData req, int auctionId)
    {
        var auction = await _db.Auctions.FindAsync(auctionId);
        if (auction == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var body = await req.ReadFromJsonAsync<ImportLotsRequest>();
        if (body == null || body.LotNumbers == null || body.LotNumbers.Count == 0)
        {
            var resp400 = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await resp400.WriteStringAsync("{\"error\":\"lotNumbers required\"}");
            return resp400;
        }

        var auctionNum = auction.AuctionNumber;
        var lotsCsv = string.Join(",", body.LotNumbers);

        try
        {
            var conn = _catalogDb.Database.GetConnectionString();
            using var sqlConn = new Microsoft.Data.SqlClient.SqlConnection(conn);
            await sqlConn.OpenAsync();

            // 1. Create auction.["261.Lots"] — full copy of dbo.CatalogLots for selected lots
            var lotsTable = $"{auctionNum}.Lots";
            await ExecuteSql(sqlConn, $@"
                IF OBJECT_ID('auction.[{lotsTable}]', 'U') IS NOT NULL DROP TABLE auction.[{lotsTable}];
                SELECT * INTO auction.[{lotsTable}] FROM auction.cataloglots WHERE LotNumber IN ({lotsCsv});
            ");

            // 2. Get all box numbers from the imported lots
            var boxNumbersSql = $@"
                SELECT DISTINCT value AS BoxNumber
                FROM auction.[{lotsTable}]
                CROSS APPLY STRING_SPLIT(IncludedBoxNumbers, ',')
                WHERE ISNUMERIC(LTRIM(RTRIM(value))) = 1 AND CAST(LTRIM(RTRIM(value)) AS INT) > 0
            ";

            // 3. Create auction.["261.Skins"] — full copy of dbo.SkinTable for boxes in those lots
            var skinsTable = $"{auctionNum}.Skins";
            await ExecuteSql(sqlConn, $@"
                IF OBJECT_ID('auction.[{skinsTable}]', 'U') IS NOT NULL DROP TABLE auction.[{skinsTable}];
                SELECT s.* INTO auction.[{skinsTable}]
                FROM dbo.SkinTable s
                WHERE s.BoxNumber IN (
                    SELECT CAST(LTRIM(RTRIM(value)) AS INT)
                    FROM auction.[{lotsTable}]
                    CROSS APPLY STRING_SPLIT(IncludedBoxNumbers, ',')
                    WHERE ISNUMERIC(LTRIM(RTRIM(value))) = 1 AND CAST(LTRIM(RTRIM(value)) AS INT) > 0
                );
            ");

            // 4. Create auction.["261.Boxes"] — aggregated box view + location from boxstatingfromkphg
            var boxesTable = $"{auctionNum}.Boxes";
            await ExecuteSql(sqlConn, $@"
                IF OBJECT_ID('auction.[{boxesTable}]', 'U') IS NOT NULL DROP TABLE auction.[{boxesTable}];
                SELECT
                    s.BoxNumber, s.BoxType, s.BoxStatus, s.SalesType, s.[Group], s.Gender,
                    s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages,
                    COUNT(*) AS Skins,
                    ISNULL(b.BoxLocation, '') AS BoxLocation
                INTO auction.[{boxesTable}]
                FROM auction.[{skinsTable}] s
                LEFT JOIN dbo.boxstatingfromkphg b ON b.BoxNumber = s.BoxNumber
                GROUP BY s.BoxNumber, s.BoxType, s.BoxStatus, s.SalesType, s.[Group], s.Gender,
                    s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages,
                    b.BoxLocation;
            ");

            // Get counts
            var lotCount = await GetScalar(sqlConn, $"SELECT COUNT(*) FROM auction.[{lotsTable}]");
            var boxCount = await GetScalar(sqlConn, $"SELECT COUNT(*) FROM auction.[{boxesTable}]");
            var skinCount = await GetScalar(sqlConn, $"SELECT COUNT(*) FROM auction.[{skinsTable}]");

            _logger.LogInformation("Imported lots for auction {AuctionNum}: {Lots} lots, {Boxes} boxes, {Skins} skins",
                auctionNum, lotCount, boxCount, skinCount);

            return await CreateJsonResponse(req, new
            {
                success = true,
                auctionNumber = auctionNum,
                tables = new { lots = lotsTable, boxes = boxesTable, skins = skinsTable },
                counts = new { lots = lotCount, boxes = boxCount, skins = skinCount }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import lots for auction {AuctionId}", auctionId);
            return await CreateJsonResponse(req, new { error = ex.Message }, System.Net.HttpStatusCode.InternalServerError);
        }
    }

    private static async Task ExecuteSql(Microsoft.Data.SqlClient.SqlConnection conn, string sql)
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> GetScalar(Microsoft.Data.SqlClient.SqlConnection conn, string sql)
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
        cmd.CommandTimeout = 60;
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    [Function("DeleteAuction")]
    public async Task<HttpResponseData> DeleteAuction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "auctions/{auctionId:int}")] HttpRequestData req, int auctionId)
    {
        var auction = await _db.Auctions.Include(a => a.Lots).FirstOrDefaultAsync(a => a.Id == auctionId);
        if (auction == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (auction.Status != AuctionStatus.Draft)
            return await CreateJsonResponse(req, new { error = "Can only delete auctions in Draft status" }, System.Net.HttpStatusCode.BadRequest);

        var soldLots = await _db.AuctionResults.AnyAsync(r => auction.Lots.Select(l => l.LotNumber).Contains(r.LotNumber));
        if (soldLots)
            return await CreateJsonResponse(req, new { error = "Cannot delete auction with sold lots" }, System.Net.HttpStatusCode.BadRequest);

        var auctionNum = auction.AuctionNumber;

        // Delete related data in FK order
        var lotIds = auction.Lots.Select(l => l.Id).ToList();
        var lotNumbers = auction.Lots.Select(l => l.LotNumber).ToList();

        if (lotIds.Count > 0)
        {
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.PackedBoxes WHERE PackingOrderLineId IN (SELECT Id FROM auction.PackingOrderLines WHERE PackingOrderId IN (SELECT Id FROM auction.PackingOrders WHERE ShipmentId IN (SELECT Id FROM auction.Shipments WHERE Id IN (SELECT ShipmentId FROM auction.ShipmentLines WHERE LotId IN (SELECT Id FROM auction.Lots WHERE AuctionId = {auctionId})))))");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.PackingOrderLines WHERE PackingOrderId IN (SELECT Id FROM auction.PackingOrders WHERE ShipmentId IN (SELECT Id FROM auction.Shipments WHERE Id IN (SELECT ShipmentId FROM auction.ShipmentLines WHERE LotId IN (SELECT Id FROM auction.Lots WHERE AuctionId = {auctionId}))))");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.PackingOrders WHERE ShipmentId IN (SELECT Id FROM auction.Shipments WHERE Id IN (SELECT ShipmentId FROM auction.ShipmentLines WHERE LotId IN (SELECT Id FROM auction.Lots WHERE AuctionId = {auctionId})))");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.ShipmentLines WHERE LotId IN (SELECT Id FROM auction.Lots WHERE AuctionId = {auctionId})");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.Shipments WHERE Id NOT IN (SELECT DISTINCT ShipmentId FROM auction.ShipmentLines)");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.InvoiceLines WHERE LotNumber IN ({string.Join(",", lotNumbers)})");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.Invoices WHERE Id NOT IN (SELECT DISTINCT InvoiceId FROM auction.InvoiceLines)");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.LotAllocations WHERE LotId IN (SELECT Id FROM auction.Lots WHERE AuctionId = {auctionId})");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.TypistEntries WHERE LotNumber IN ({string.Join(",", lotNumbers)})");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.AuctionTransactions WHERE LotNumber IN ({string.Join(",", lotNumbers)})");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.AuctionResults WHERE LotNumber IN ({string.Join(",", lotNumbers)})");
        }

        _db.Lots.RemoveRange(auction.Lots);
        _db.Auctions.Remove(auction);
        await _db.SaveChangesAsync();

        // Drop snapshot tables
        if (!string.IsNullOrEmpty(auctionNum))
        {
            try
            {
                var conn = _catalogDb.Database.GetConnectionString();
                using var sqlConn = new Microsoft.Data.SqlClient.SqlConnection(conn);
                await sqlConn.OpenAsync();
                foreach (var suffix in new[] { "Lots", "Boxes", "Skins" })
                    await ExecuteSql(sqlConn, $"IF OBJECT_ID('auction.[{auctionNum}.{suffix}]', 'U') IS NOT NULL DROP TABLE auction.[{auctionNum}.{suffix}]");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to drop snapshot tables for auction {Num}", auctionNum); }
        }

        return await CreateJsonResponse(req, new { success = true, auctionNumber = auctionNum });
    }

    [Function("ResetAllData")]
    public async Task<HttpResponseData> ResetAllData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "system/reset-all")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var keepEntities = query["keepEntities"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        if (keepEntities)
            _logger.LogWarning("RESETTING TRANSACTION DATA (keeping Brokers, Buyers, Farmers, AppUsers, SystemParameters)");
        else
            _logger.LogWarning("RESETTING ALL DATA (except SystemParameters)");

        try
        {
            // Transaction tables (always deleted) — order matters for FK dependencies
            var transactionTables = new[] {
                "PackedBoxes", "PackingOrderLines", "PackingOrders",
                "ShipmentLines", "Shipments",
                "LotSalesHistories", "AuctionTransactions", "TypistEntries",
                "InvoiceLines", "Invoices",
                "TakebackRequests", "LotAllocations", "AuctionResults", "Settlements", "Bids", "Lots", "Auctions",
                "BrokerCustomerRequests" };

            // Entity tables (only deleted if keepEntities=false)
            var entityTables = new[] { "BrokerBuyers", "Buyers", "Brokers", "Farmers", "AppUsers",
                "ShippingAddresses", "Shippers", "BoxTypeDimensions" };

            var allTables = keepEntities ? transactionTables : transactionTables.Concat(entityTables).ToArray();

            // Disable FK constraints on ALL tables (including entity tables for FK references)
            var constraintTables = transactionTables.Concat(entityTables).ToArray();
            foreach (var t in constraintTables)
                await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE auction.[{t}] NOCHECK CONSTRAINT ALL");

            // Delete data
            foreach (var t in allTables)
                await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.[{t}]");

            // Re-enable FK constraints
            foreach (var t in constraintTables)
                await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE auction.[{t}] WITH CHECK CHECK CONSTRAINT ALL");

            // Drop auction snapshot tables (e.g. auction.[261.Lots], [261.Boxes], [261.Skins])
            try
            {
                var snapshotTables = await _db.Database
                    .SqlQueryRaw<string>("SELECT QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name) AS Value FROM sys.tables WHERE schema_id = SCHEMA_ID('auction') AND (name LIKE '%.Lots' OR name LIKE '%.Boxes' OR name LIKE '%.Skins')")
                    .ToListAsync();
                foreach (var tbl in snapshotTables)
                    await _db.Database.ExecuteSqlRawAsync($"DROP TABLE {tbl}");
                _logger.LogInformation("Dropped {Count} auction snapshot tables", snapshotTables.Count);
            }
            catch (Exception ex2) { _logger.LogWarning(ex2, "Failed to drop snapshot tables"); }

            var brokerCount = await _db.Brokers.CountAsync();
            var buyerCount = await _db.Buyers.CountAsync();
            var farmerCount = await _db.Farmers.CountAsync();
            var auctionCount = await _db.Auctions.CountAsync();
            var paramCount = await _db.SystemParameters.CountAsync();
            var userCount = keepEntities ? await _db.AppUsers.CountAsync() : 0;

            _logger.LogWarning("Reset complete");

            var message = keepEntities
                ? "Transaction data reset (Brokers, Buyers, Farmers, AppUsers, SystemParameters kept)"
                : "All data reset (SystemParameters kept)";

            return await CreateJsonResponse(req, new
            {
                message,
                remaining = new
                {
                    brokers = brokerCount,
                    buyers = buyerCount,
                    farmers = farmerCount,
                    appUsers = userCount,
                    auctions = auctionCount,
                    systemParameters = paramCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset data");
            return await CreateJsonResponse(req, new { error = ex.Message }, System.Net.HttpStatusCode.InternalServerError);
        }
    }

    private static async Task<HttpResponseData> CreateJsonResponse<T>(
        HttpRequestData req, T data, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
        return response;
    }
}

public record HammerRequest(decimal HammerPrice, int WinningBrokerId);
public record ImportLotsRequest(List<int> LotNumbers);
