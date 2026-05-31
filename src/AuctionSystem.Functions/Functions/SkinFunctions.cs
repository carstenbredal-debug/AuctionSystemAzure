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
        var filter = query["filter"] ?? "all"; // all, sold, unsold

        // Get all skins
        var skins = await _catalogDb.Skins
            .Where(s => s.IsActive)
            .OrderBy(s => s.BoxNumber)
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

        // Determine which boxes are sold by checking auction results
        // A box is "sold" if it appears in a catalog lot that has been sold in an auction
        var soldBoxNumbers = await GetSoldBoxNumbersAsync();

        foreach (var skin in skins)
        {
            skin.IsSold = soldBoxNumbers.Contains(skin.BoxNumber);
        }

        // Apply filter
        var filtered = filter switch
        {
            "sold" => skins.Where(s => s.IsSold).ToList(),
            "unsold" => skins.Where(s => !s.IsSold).ToList(),
            _ => skins
        };

        var summary = new
        {
            totalSkins = skins.Count,
            soldSkins = skins.Count(s => s.IsSold),
            unsoldSkins = skins.Count(s => !s.IsSold),
            skins = filtered
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(summary, JsonOptions));
        return response;
    }

    private async Task<HashSet<int>> GetSoldBoxNumbersAsync()
    {
        // Get lot numbers that have been sold (SoldToBuyerId is set)
        var soldLotNumbers = await _auctionDb.AuctionResults
            .Where(r => r.SoldToBuyerId != null)
            .Select(r => r.LotNumber)
            .ToListAsync();

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
