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
        var filter = query["filter"] ?? "all";
        var page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Clamp(ps, 10, 500) : 100;
        var search = query["search"]?.Trim();

        // Get sold box numbers (cached per request — small set)
        var soldBoxNumbers = await GetSoldBoxNumbersAsync();

        // Build base query
        IQueryable<Domain.Entities.Skin> baseQuery = _catalogDb.Skins.Where(s => s.IsActive);

        // Apply search filter
        if (!string.IsNullOrEmpty(search))
        {
            baseQuery = baseQuery.Where(s =>
                s.Farmer!.Contains(search) ||
                s.Farm!.Contains(search) ||
                s.Barcode.ToString().Contains(search) ||
                s.BoxNumber.ToString().Contains(search));
        }

        // Get total counts (using COUNT at DB level)
        int totalCount = await baseQuery.CountAsync();

        // For sold/unsold counts, we need box-level filtering
        // Get distinct box numbers that are sold
        int soldCount, unsoldCount;
        if (soldBoxNumbers.Count == 0)
        {
            soldCount = 0;
            unsoldCount = totalCount;
        }
        else
        {
            soldCount = await baseQuery.Where(s => soldBoxNumbers.Contains(s.BoxNumber)).CountAsync();
            unsoldCount = totalCount - soldCount;
        }

        // Apply sold/unsold filter
        IQueryable<Domain.Entities.Skin> filteredQuery = filter switch
        {
            "sold" => baseQuery.Where(s => soldBoxNumbers.Contains(s.BoxNumber)),
            "unsold" => baseQuery.Where(s => !soldBoxNumbers.Contains(s.BoxNumber)),
            _ => baseQuery
        };

        var filteredCount = filter switch
        {
            "sold" => soldCount,
            "unsold" => unsoldCount,
            _ => totalCount
        };

        // Apply pagination
        var skins = await filteredQuery
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
                BoxStatus = s.BoxStatus,
                SalesType = s.SalesType,
                Gender = s.Gender,
                Group = s.Group,
                Size = s.Size,
                HairLength = s.HairLength,
                Color = s.Color,
                Quality = s.Quality,
                Clarity = s.Clarity,
                Damages = s.Damages,
                Auction = s.Auction,
                Location = s.Location,
                LastChangedAt = s.LastChangedAt
            })
            .ToListAsync();

        // Set IsSold flag on returned page
        foreach (var skin in skins)
        {
            skin.IsSold = soldBoxNumbers.Contains(skin.BoxNumber);
        }

        var result = new
        {
            totalSkins = totalCount,
            soldSkins = soldCount,
            unsoldSkins = unsoldCount,
            filteredCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)filteredCount / pageSize),
            skins
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    private async Task<HashSet<int>> GetSoldBoxNumbersAsync()
    {
        // Get lot numbers that have been sold (SoldToBuyerId is set)
        var soldLotNumbers = await _auctionDb.AuctionResults
            .Where(r => r.SoldToBuyerId != null)
            .Select(r => r.LotNumber)
            .ToListAsync();

        if (soldLotNumbers.Count == 0)
            return new HashSet<int>();

        // Get catalog lots matching those lot numbers
        var soldCatalogLots = await _catalogDb.CatalogLots
            .Where(cl => soldLotNumbers.Contains(cl.LotNumber))
            .Select(cl => cl.IncludedBoxNumbers)
            .ToListAsync();

        // Parse box numbers from comma-separated lists
        var soldBoxNumbers = new HashSet<int>();
        foreach (var boxList in soldCatalogLots)
        {
            if (string.IsNullOrEmpty(boxList)) continue;
            foreach (var boxStr in boxList.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(boxStr.Trim(), out var boxNum))
                    soldBoxNumbers.Add(boxNum);
            }
        }

        return soldBoxNumbers;
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
    public string? BoxStatus { get; set; }
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? Size { get; set; }
    public string? HairLength { get; set; }
    public string? Color { get; set; }
    public string? Quality { get; set; }
    public string? Clarity { get; set; }
    public string? Damages { get; set; }
    public string? Auction { get; set; }
    public string? Location { get; set; }
    public DateTime? LastChangedAt { get; set; }
    public bool IsSold { get; set; }
}
