using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class SkinFunctions
{
    private readonly CatalogDbContext _catalogDb;
    private readonly AuctionDbContext _auctionDb;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SkinFunctions(CatalogDbContext catalogDb, AuctionDbContext auctionDb)
    {
        _catalogDb = catalogDb;
        _auctionDb = auctionDb;
    }

    [Function("GetSkins")]
    public async Task<HttpResponseData> GetSkins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Clamp(ps, 10, 500) : 100;
        var search = query["search"]?.Trim();
        var auctionIdStr = query["auctionId"];
        int? auctionId = int.TryParse(auctionIdStr, out var aid) ? aid : null;

        // Build sold box → sale info lookup
        var saleInfoByBox = await GetSoldBoxSaleInfoAsync(auctionId);
        var soldBoxNumbers = saleInfoByBox.Keys.ToHashSet();

        // Query only sold skins
        IQueryable<Domain.Entities.Skin> baseQuery = _catalogDb.Skins
            .Where(s => s.IsActive && soldBoxNumbers.Contains(s.BoxNumber));

        // Apply search
        if (!string.IsNullOrEmpty(search))
        {
            baseQuery = baseQuery.Where(s =>
                s.Farmer!.Contains(search) ||
                s.Farm!.Contains(search) ||
                s.Barcode.ToString().Contains(search) ||
                s.BoxNumber.ToString().Contains(search));
        }

        int totalCount = await baseQuery.CountAsync();

        // Paginate
        var skins = await baseQuery
            .OrderBy(s => s.BoxNumber)
            .ThenBy(s => s.Barcode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SkinDto
            {
                UniqueID = s.UniqueID,
                BoxNumber = s.BoxNumber,
                Barcode = s.Barcode,
                Farmer = s.Farmer,
                Farm = s.Farm,
                BoxType = s.BoxType,
                SalesType = s.SalesType,
                Gender = s.Gender,
                Group = s.Group,
                Size = s.Size,
                Color = s.Color,
                Quality = s.Quality,
                Clarity = s.Clarity,
                Damages = s.Damages,
                Auction = s.Auction,
                LastChangedAt = s.LastChangedAt
            })
            .ToListAsync();

        // Attach sale info
        foreach (var skin in skins)
        {
            if (saleInfoByBox.TryGetValue(skin.BoxNumber, out var info))
            {
                skin.BrokerName = info.BrokerName;
                skin.BuyerName = info.BuyerName;
                skin.PriceEur = info.PriceEur;
                skin.LotNumber = info.LotNumber;
            }
        }

        var result = new
        {
            totalSkins = totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            skins
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    [Function("CheckSkinsPriceIntegrity")]
    public async Task<HttpResponseData> CheckSkinsPriceIntegrity(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diag/skins-price-check")] HttpRequestData req)
    {
        // Get all sold auction results
        var soldResults = await _auctionDb.AuctionResults
            .Where(r => r.SoldToBuyerId != null)
            .Include(r => r.Broker)
            .Include(r => r.SoldToBuyer)
            .ToListAsync();

        if (soldResults.Count == 0)
        {
            var emptyResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
            emptyResponse.Headers.Add("Content-Type", "application/json");
            await emptyResponse.WriteStringAsync(JsonSerializer.Serialize(new
            {
                message = "No sold lots found",
                mismatches = Array.Empty<object>(),
                totalChecked = 0,
                totalMismatches = 0
            }, JsonOptions));
            return emptyResponse;
        }

        // Get catalog lots for sold lot numbers
        var soldLotNumbers = soldResults.Select(r => r.LotNumber).Distinct().ToList();
        var catalogLots = await _catalogDb.CatalogLots
            .Where(cl => soldLotNumbers.Contains(cl.LotNumber))
            .ToListAsync();

        // Build lot → box numbers mapping
        var lotBoxes = new Dictionary<int, List<int>>();
        foreach (var cl in catalogLots)
        {
            if (string.IsNullOrEmpty(cl.IncludedBoxNumbers)) continue;
            var boxes = cl.IncludedBoxNumbers
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0)
                .Where(n => n > 0)
                .ToList();
            lotBoxes[cl.LotNumber] = boxes;
        }

        // Get all relevant box numbers and count skins per box
        var allBoxNumbers = lotBoxes.Values.SelectMany(b => b).Distinct().ToList();
        var skinsPerBox = await _catalogDb.Skins
            .Where(s => s.IsActive && allBoxNumbers.Contains(s.BoxNumber))
            .GroupBy(s => s.BoxNumber)
            .Select(g => new { BoxNumber = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BoxNumber, x => x.Count);

        // Check each sold lot
        var mismatches = new List<object>();
        foreach (var result in soldResults)
        {
            if (!lotBoxes.TryGetValue(result.LotNumber, out var boxes)) continue;

            var actualSkinCount = boxes.Sum(b => skinsPerBox.GetValueOrDefault(b, 0));
            var expectedHammerPrice = result.TotalSkins * result.PriceEur;
            var actualHammerPrice = actualSkinCount * result.PriceEur;

            if (actualSkinCount != result.TotalSkins)
            {
                mismatches.Add(new
                {
                    lotNumber = result.LotNumber,
                    broker = result.Broker.CompanyName,
                    buyer = result.SoldToBuyer?.Name,
                    pricePerSkin = result.PriceEur,
                    expectedSkins = result.TotalSkins,
                    actualSkins = actualSkinCount,
                    expectedHammerPrice,
                    actualHammerPrice,
                    difference = actualHammerPrice - expectedHammerPrice
                });
            }
        }

        var checkResult = new
        {
            message = mismatches.Count == 0
                ? $"All {soldResults.Count} sold lots match — skin counts and hammer prices are consistent"
                : $"{mismatches.Count} mismatch(es) found out of {soldResults.Count} sold lots",
            totalChecked = soldResults.Count,
            totalMismatches = mismatches.Count,
            mismatches
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(checkResult, JsonOptions));
        return response;
    }

    [Function("GetFarmerAuctionDetail")]
    public async Task<HttpResponseData> GetFarmerAuctionDetail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins/farmer-auction-detail")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var farmerName = query["farmerName"]?.Trim();
        var auctionIdStr = query["auctionId"];
        int? auctionId = int.TryParse(auctionIdStr, out var aid) ? aid : null;

        if (string.IsNullOrEmpty(farmerName) || auctionId == null)
        {
            var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("farmerName and auctionId query parameters are required");
            return badResponse;
        }

        var auction = await _auctionDb.Auctions.FindAsync(auctionId.Value);
        if (auction == null)
        {
            var notFound = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Auction not found");
            return notFound;
        }

        // Read from auction snapshot table: auction.[{auctionNumber}.Skins]
        var skinsTable = $"auction.[{auction.AuctionNumber}.Skins]";
        var connStr = _auctionDb.Database.GetConnectionString()!;

        int totalSkins = 0;
        int totalRowsInTable = 0;
        int distinctFarmers = 0;
        var skinsByBox = new Dictionary<int, int>(); // boxNumber → count

        await using (var conn = new SqlConnection(connStr))
        {
            await conn.OpenAsync();

            // Debug: total rows in snapshot table
            await using (var countCmd = new SqlCommand($"SELECT COUNT(*) FROM {skinsTable}", conn))
            {
                totalRowsInTable = (int)await countCmd.ExecuteScalarAsync();
            }

            // Debug: count of distinct farmers
            await using (var farmerCmd = new SqlCommand($"SELECT COUNT(DISTINCT Farmer) FROM {skinsTable}", conn))
            {
                distinctFarmers = (int)await farmerCmd.ExecuteScalarAsync();
            }

            // Total skins for this farmer in the snapshot
            await using (var cmd = new SqlCommand($"SELECT BoxNumber, COUNT(*) AS Cnt FROM {skinsTable} WHERE Farmer = @farmer GROUP BY BoxNumber", conn))
            {
                cmd.Parameters.AddWithValue("@farmer", farmerName);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var box = reader.GetInt32(0);
                    var cnt = reader.GetInt32(1);
                    skinsByBox[box] = cnt;
                    totalSkins += cnt;
                }
            }
        }

        // Get sold box info for this auction
        var saleInfoByBox = await GetSoldBoxSaleInfoAsync(auctionId.Value);

        var soldSkinCount = 0;
        decimal totalValue = 0;
        foreach (var (box, count) in skinsByBox)
        {
            if (saleInfoByBox.TryGetValue(box, out var info))
            {
                soldSkinCount += count;
                totalValue += count * info.PriceEur;
            }
        }

        var result = new
        {
            auctionId = auctionId.Value,
            auctionNumber = auction.AuctionNumber,
            farmerName,
            totalSkins,
            soldSkins = soldSkinCount,
            totalValue,
            _debug = new { snapshotTable = skinsTable, totalRowsInTable, distinctFarmers, farmerBoxCount = skinsByBox.Count }
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    [Function("GetFarmerAuctionSummary")]
    public async Task<HttpResponseData> GetFarmerAuctionSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins/farmer-summary")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var farmerName = query["farmerName"]?.Trim();

        if (string.IsNullOrEmpty(farmerName))
        {
            var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("farmerName query parameter is required");
            return badResponse;
        }

        // Get all auctions
        var auctions = await _auctionDb.Auctions.ToListAsync();
        var connStr = _auctionDb.Database.GetConnectionString()!;
        var summaries = new List<object>();

        foreach (var auction in auctions)
        {
            var skinsTable = $"auction.[{auction.AuctionNumber}.Skins]";
            var skinsByBox = new Dictionary<int, int>();
            int totalSkins = 0;

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                await using var cmd = new SqlCommand($"SELECT BoxNumber, COUNT(*) AS Cnt FROM {skinsTable} WHERE Farmer = @farmer GROUP BY BoxNumber", conn);
                cmd.Parameters.AddWithValue("@farmer", farmerName);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var box = reader.GetInt32(0);
                    var cnt = reader.GetInt32(1);
                    skinsByBox[box] = cnt;
                    totalSkins += cnt;
                }
            }
            catch
            {
                // Snapshot table may not exist for this auction
                continue;
            }

            if (totalSkins == 0) continue;

            var saleInfoByBox = await GetSoldBoxSaleInfoAsync(auction.Id);

            var soldSkinCount = 0;
            decimal totalValue = 0;
            foreach (var (box, count) in skinsByBox)
            {
                if (saleInfoByBox.TryGetValue(box, out var info))
                {
                    soldSkinCount += count;
                    totalValue += count * info.PriceEur;
                }
            }

            summaries.Add(new
            {
                auctionId = auction.Id,
                auction = auction.AuctionNumber,
                totalSkins,
                soldSkins = soldSkinCount,
                totalValue
            });
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(summaries, JsonOptions));
        return response;
    }

    private async Task<Dictionary<int, BoxSaleInfo>> GetSoldBoxSaleInfoAsync(int? auctionId = null)
    {
        // Get sold auction results with broker and buyer info
        var resultsQuery = _auctionDb.AuctionResults
            .Where(r => r.SoldToBuyerId != null);

        if (auctionId.HasValue)
        {
            var auctionLotNumbers = await _auctionDb.Lots
                .Where(l => l.AuctionId == auctionId.Value)
                .Select(l => l.LotNumber)
                .ToListAsync();
            resultsQuery = resultsQuery.Where(r => auctionLotNumbers.Contains(r.LotNumber));
        }

        var soldResults = await resultsQuery
            .Include(r => r.Broker)
            .Include(r => r.SoldToBuyer)
            .Select(r => new
            {
                r.LotNumber,
                r.PriceEur,
                BrokerName = r.Broker.CompanyName,
                BuyerName = r.SoldToBuyer!.Name
            })
            .ToListAsync();

        if (soldResults.Count == 0)
            return new Dictionary<int, BoxSaleInfo>();

        // Get catalog lots to map lot numbers to box numbers
        var soldLotNumbers = soldResults.Select(r => r.LotNumber).Distinct().ToList();
        var catalogLots = await _catalogDb.CatalogLots
            .Where(cl => soldLotNumbers.Contains(cl.LotNumber))
            .Select(cl => new { cl.LotNumber, cl.IncludedBoxNumbers })
            .ToListAsync();

        // Build lookup: lot number → sale info
        var lotSaleInfo = soldResults.ToDictionary(r => r.LotNumber, r => r);

        // Build lookup: box number → sale info
        var boxSaleInfo = new Dictionary<int, BoxSaleInfo>();
        foreach (var lot in catalogLots)
        {
            if (string.IsNullOrEmpty(lot.IncludedBoxNumbers)) continue;
            if (!lotSaleInfo.TryGetValue(lot.LotNumber, out var sale)) continue;

            foreach (var boxStr in lot.IncludedBoxNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(boxStr.Trim(), out var boxNum) && !boxSaleInfo.ContainsKey(boxNum))
                {
                    boxSaleInfo[boxNum] = new BoxSaleInfo
                    {
                        BrokerName = sale.BrokerName,
                        BuyerName = sale.BuyerName,
                        PriceEur = sale.PriceEur,
                        LotNumber = lot.LotNumber
                    };
                }
            }
        }

        return boxSaleInfo;
    }

    private class BoxSaleInfo
    {
        public string BrokerName { get; set; } = "";
        public string BuyerName { get; set; } = "";
        public decimal PriceEur { get; set; }
        public int LotNumber { get; set; }
    }
}

public class SkinDto
{
    public Guid UniqueID { get; set; }
    public int BoxNumber { get; set; }
    public long Barcode { get; set; }
    public string? Farmer { get; set; }
    public string? Farm { get; set; }
    public string? BoxType { get; set; }
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public string? Quality { get; set; }
    public string? Clarity { get; set; }
    public string? Damages { get; set; }
    public string? Auction { get; set; }
    public DateTime? LastChangedAt { get; set; }
    public string? BrokerName { get; set; }
    public string? BuyerName { get; set; }
    public decimal? PriceEur { get; set; }
    public int? LotNumber { get; set; }
}
