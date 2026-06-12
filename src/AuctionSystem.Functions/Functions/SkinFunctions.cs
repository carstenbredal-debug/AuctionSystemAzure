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
        var skinsByBox = new Dictionary<int, int>(); // boxNumber → count

        await using (var conn = new SqlConnection(connStr))
        {
            await conn.OpenAsync();

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
            totalValue
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    [Function("GetFarmerAuctionLots")]
    public async Task<HttpResponseData> GetFarmerAuctionLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins/farmer-auction-lots")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var farmerName = query["farmerName"]?.Trim();
        var auctionIdStr = query["auctionId"];
        int? auctionId = int.TryParse(auctionIdStr, out var aid) ? aid : null;

        if (string.IsNullOrEmpty(farmerName) || auctionId == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var auction = await _auctionDb.Auctions.FindAsync(auctionId.Value);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var skinsTable = $"auction.[{auction.AuctionNumber}.Skins]";
        var lotsTable = $"auction.[{auction.AuctionNumber}.Lots]";
        var connStr = _auctionDb.Database.GetConnectionString()!;

        var saleInfoByBox = await GetSoldBoxSaleInfoAsync(auctionId.Value);

        // Pre-load farmer's skin count per box from snapshot
        var farmerSkinsByBox = new Dictionary<int, int>();
        await using (var preConn = new SqlConnection(connStr))
        {
            await preConn.OpenAsync();
            await using var preCmd = new SqlCommand($"SELECT BoxNumber, COUNT(*) FROM {skinsTable} WHERE Farmer = @farmer GROUP BY BoxNumber", preConn);
            preCmd.Parameters.AddWithValue("@farmer", farmerName);
            await using var preReader = await preCmd.ExecuteReaderAsync();
            while (await preReader.ReadAsync())
                farmerSkinsByBox[preReader.GetInt32(0)] = preReader.GetInt32(1);
        }

        // Load all lots (fast, no correlated subquery)
        var lots = new List<object>();
        var farmerBoxNumbers = new HashSet<int>(farmerSkinsByBox.Keys);

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand($@"
            SELECT l.LotNumber, l.IncludedBoxNumbers,
                   l.SalesType, l.Gender, l.[Group], l.Quality, l.HairLength, l.Size, l.Color, l.Clarity
            FROM {lotsTable} l
            ORDER BY l.LotNumber", conn);
        cmd.CommandTimeout = 60;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var lotNumber = reader.GetInt32(0);
            var boxNumbersCsv = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var boxNumbers = boxNumbersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0).ToList();

            // Count only farmer's skins in this lot's boxes
            var farmerSkins = 0;
            foreach (var bn in boxNumbers)
                if (farmerSkinsByBox.TryGetValue(bn, out var cnt))
                    farmerSkins += cnt;

            if (farmerSkins == 0) continue; // Skip lots with no farmer skins

            var isSold = boxNumbers.Any(b => saleInfoByBox.ContainsKey(b));

            // Calculate value from farmer's skins in SOLD boxes only
            decimal soldValue = 0;
            foreach (var bn in boxNumbers)
            {
                if (saleInfoByBox.TryGetValue(bn, out var info) && farmerSkinsByBox.TryGetValue(bn, out var skinCount))
                    soldValue += skinCount * info.PriceEur;
            }

            lots.Add(new
            {
                lotNumber,
                totalSkins = farmerSkins,
                salesType = reader.IsDBNull(2) ? null : reader.GetString(2),
                gender = reader.IsDBNull(3) ? null : reader.GetString(3),
                group = reader.IsDBNull(4) ? null : reader.GetString(4),
                quality = reader.IsDBNull(5) ? null : reader.GetString(5),
                hairLength = reader.IsDBNull(6) ? null : reader.GetString(6),
                size = reader.IsDBNull(7) ? null : reader.GetString(7),
                color = reader.IsDBNull(8) ? null : reader.GetString(8),
                clarity = reader.IsDBNull(9) ? null : reader.GetString(9),
                status = isSold ? "Sold" : "Auction",
                soldValue = soldValue > 0 ? soldValue : (decimal?)null
            });
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(lots, JsonOptions));
        return response;
    }

    [Function("GetFarmerAuctionBoxes")]
    public async Task<HttpResponseData> GetFarmerAuctionBoxes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins/farmer-auction-boxes")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var farmerName = query["farmerName"]?.Trim();
        var auctionIdStr = query["auctionId"];
        var lotNumberStr = query["lotNumber"];
        int? auctionId = int.TryParse(auctionIdStr, out var aid) ? aid : null;
        int? lotNumber = int.TryParse(lotNumberStr, out var ln) ? ln : null;

        if (string.IsNullOrEmpty(farmerName) || auctionId == null || lotNumber == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var auction = await _auctionDb.Auctions.FindAsync(auctionId.Value);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var skinsTable = $"auction.[{auction.AuctionNumber}.Skins]";
        var lotsTable = $"auction.[{auction.AuctionNumber}.Lots]";
        var connStr = _auctionDb.Database.GetConnectionString()!;

        var saleInfoByBox = await GetSoldBoxSaleInfoAsync(auctionId.Value);

        // Get box numbers for this lot
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        string? boxNumbersCsv = null;
        await using (var lotCmd = new SqlCommand($"SELECT IncludedBoxNumbers FROM {lotsTable} WHERE LotNumber = @lot", conn))
        {
            lotCmd.Parameters.AddWithValue("@lot", lotNumber.Value);
            boxNumbersCsv = (await lotCmd.ExecuteScalarAsync()) as string;
        }

        if (string.IsNullOrEmpty(boxNumbersCsv))
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var boxNumbers = boxNumbersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0).ToList();

        // Get skins per box for this farmer
        var boxes = new List<object>();
        await using var cmd = new SqlCommand($@"
            SELECT BoxNumber, BoxType, COUNT(*) AS SkinCount
            FROM {skinsTable}
            WHERE Farmer = @farmer AND BoxNumber IN ({string.Join(",", boxNumbers)})
            GROUP BY BoxNumber, BoxType
            ORDER BY BoxNumber", conn);
        cmd.Parameters.AddWithValue("@farmer", farmerName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var boxNum = reader.GetInt32(0);
            var isSold = saleInfoByBox.ContainsKey(boxNum);
            var price = isSold ? saleInfoByBox[boxNum].PriceEur : 0;

            boxes.Add(new
            {
                boxNumber = boxNum,
                boxType = reader.IsDBNull(1) ? null : reader.GetString(1),
                skinCount = reader.GetInt32(2),
                status = isSold ? "Sold" : "Auction",
                pricePerSkin = isSold ? price : (decimal?)null,
                value = isSold ? reader.GetInt32(2) * price : (decimal?)null
            });
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(boxes, JsonOptions));
        return response;
    }

    [Function("GetFarmerAuctionSkins")]
    public async Task<HttpResponseData> GetFarmerAuctionSkins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins/farmer-auction-skins")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var farmerName = query["farmerName"]?.Trim();
        var auctionIdStr = query["auctionId"];
        var lotNumberStr = query["lotNumber"];
        int? auctionId = int.TryParse(auctionIdStr, out var aid) ? aid : null;
        int? lotNumber = int.TryParse(lotNumberStr, out var ln) ? ln : null;

        if (string.IsNullOrEmpty(farmerName) || auctionId == null || lotNumber == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var auction = await _auctionDb.Auctions.FindAsync(auctionId.Value);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var skinsTable = $"auction.[{auction.AuctionNumber}.Skins]";
        var lotsTable = $"auction.[{auction.AuctionNumber}.Lots]";
        var connStr = _auctionDb.Database.GetConnectionString()!;

        var saleInfoByBox = await GetSoldBoxSaleInfoAsync(auctionId.Value);

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        // Get box numbers for this lot
        string? boxNumbersCsv = null;
        await using (var lotCmd = new SqlCommand($"SELECT IncludedBoxNumbers FROM {lotsTable} WHERE LotNumber = @lot", conn))
        {
            lotCmd.Parameters.AddWithValue("@lot", lotNumber.Value);
            boxNumbersCsv = (await lotCmd.ExecuteScalarAsync()) as string;
        }

        if (string.IsNullOrEmpty(boxNumbersCsv))
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var boxNumbers = boxNumbersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0).ToList();

        var skins = new List<object>();
        await using var cmd = new SqlCommand($@"
            SELECT Barcode, BoxNumber, BoxType, SalesType, Gender, [Group], Size, Color, Quality, Clarity, Damages, HairLength
            FROM {skinsTable}
            WHERE Farmer = @farmer AND BoxNumber IN ({string.Join(",", boxNumbers)})
            ORDER BY BoxNumber, Barcode", conn);
        cmd.Parameters.AddWithValue("@farmer", farmerName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var boxNum = reader.GetInt32(1);
            var isSold = saleInfoByBox.ContainsKey(boxNum);
            skins.Add(new
            {
                barcode = reader.GetInt64(0),
                boxNumber = boxNum,
                boxType = reader.IsDBNull(2) ? null : reader.GetString(2),
                salesType = reader.IsDBNull(3) ? null : reader.GetString(3),
                gender = reader.IsDBNull(4) ? null : reader.GetString(4),
                group = reader.IsDBNull(5) ? null : reader.GetString(5),
                size = reader.IsDBNull(6) ? null : reader.GetString(6),
                color = reader.IsDBNull(7) ? null : reader.GetString(7),
                quality = reader.IsDBNull(8) ? null : reader.GetString(8),
                clarity = reader.IsDBNull(9) ? null : reader.GetString(9),
                damages = reader.IsDBNull(10) ? null : reader.GetString(10),
                hairLength = reader.IsDBNull(11) ? null : reader.GetString(11),
                status = isSold ? "Sold" : "Auction"
            });
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(skins, JsonOptions));
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
