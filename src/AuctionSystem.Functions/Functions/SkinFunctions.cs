using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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
        var auctionNumber = query["auctionNumber"]?.Trim();

        if (string.IsNullOrEmpty(farmerName) || string.IsNullOrEmpty(auctionNumber))
        {
            var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("farmerName and auctionNumber query parameters are required");
            return badResponse;
        }

        // Get skins for this farmer in this auction
        var skins = await _catalogDb.Skins
            .Where(s => s.IsActive && s.Farmer == farmerName && s.Auction == auctionNumber)
            .ToListAsync();

        var totalSkins = skins.Count;
        var boxNumbers = skins.Select(s => s.BoxNumber).Distinct().ToList();

        // Get sold box info
        var saleInfoByBox = await GetSoldBoxSaleInfoAsync();

        var soldSkinCount = 0;
        decimal totalValue = 0;
        foreach (var box in boxNumbers.Where(b => saleInfoByBox.ContainsKey(b)))
        {
            var info = saleInfoByBox[box];
            var skinsInBox = skins.Count(s => s.BoxNumber == box);
            soldSkinCount += skinsInBox;
            totalValue += skinsInBox * info.PriceEur;
        }

        var result = new
        {
            auction = auctionNumber,
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

        // Get all skins for this farmer grouped by auction
        var farmerSkins = await _catalogDb.Skins
            .Where(s => s.IsActive && s.Farmer == farmerName)
            .GroupBy(s => s.Auction ?? "Unknown")
            .Select(g => new
            {
                Auction = g.Key,
                TotalSkins = g.Count(),
                BoxNumbers = g.Select(s => s.BoxNumber).Distinct().ToList()
            })
            .ToListAsync();

        // Get sold box info to determine which skins are sold and their value
        var saleInfoByBox = await GetSoldBoxSaleInfoAsync();

        var summaries = farmerSkins.Select(a =>
        {
            var soldBoxes = a.BoxNumbers.Where(b => saleInfoByBox.ContainsKey(b)).ToList();
            // Count sold skins (skins in sold boxes)
            var soldSkinCount = 0;
            decimal totalValue = 0;
            foreach (var box in soldBoxes)
            {
                var info = saleInfoByBox[box];
                // Count skins in this box belonging to this farmer
                var skinsInBox = _catalogDb.Skins
                    .Count(s => s.IsActive && s.BoxNumber == box && s.Farmer == farmerName);
                soldSkinCount += skinsInBox;
                totalValue += skinsInBox * info.PriceEur;
            }

            return new
            {
                auction = a.Auction,
                totalSkins = a.TotalSkins,
                soldSkins = soldSkinCount,
                totalValue
            };
        }).ToList();

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
