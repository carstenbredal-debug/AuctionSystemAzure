using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class SkinFunctions
{
    private readonly CatalogDbContext _catalogDb;
    private readonly AuctionDbContext _auctionDb;
    private readonly ILogger<SkinFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SkinFunctions(CatalogDbContext catalogDb, AuctionDbContext auctionDb, ILogger<SkinFunctions> logger)
    {
        _catalogDb = catalogDb;
        _auctionDb = auctionDb;
        _logger = logger;
    }

    [Function("GetSkins")]
    public async Task<HttpResponseData> GetSkins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "skins")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Clamp(ps, 10, 500) : 100;
        var search = query["search"]?.Trim();

        // Build sold box → sale info lookup
        var saleInfoByBox = await GetSoldBoxSaleInfoAsync();
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

    private async Task<Dictionary<int, BoxSaleInfo>> GetSoldBoxSaleInfoAsync()
    {
        // Get sold auction results with broker and buyer info
        var soldResults = await _auctionDb.AuctionResults
            .Where(r => r.SoldToBuyerId != null)
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
