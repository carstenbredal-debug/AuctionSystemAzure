using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Dapper;

namespace AuctionSystem.Functions.Functions;

public class BoxRefreshFunctions
{
    private readonly IConfiguration _config;
    private readonly ILogger<BoxRefreshFunctions> _logger;

    public BoxRefreshFunctions(IConfiguration config, ILogger<BoxRefreshFunctions> logger)
    {
        _config = config;
        _logger = logger;
    }

    [Function("RefreshBoxes")]
    public async Task<HttpResponseData> RefreshBoxes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "boxes/refresh")] HttpRequestData req)
    {
        _logger.LogInformation("Manual box refresh triggered");

        try
        {
            var count = await RefreshBoxTableAsync();
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"Refreshed {count} boxes from dbo.SkinTable");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh boxes");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync($"Error: {ex.Message}");
            return response;
        }
    }

    [Function("RefreshBoxesTimer")]
    public async Task RefreshBoxesTimer(
        [TimerTrigger("0 0 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Hourly box refresh triggered at {Time}", DateTime.UtcNow);

        try
        {
            var count = await RefreshBoxTableAsync();
            _logger.LogInformation("Hourly box refresh completed: {Count} boxes", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hourly box refresh failed");
        }
    }

    private async Task<int> RefreshBoxTableAsync()
    {
        var connStr = _config["SqlConnectionString"]
                   ?? _config["Values:SqlConnectionString"]
                   ?? throw new InvalidOperationException("SqlConnectionString not configured");

        using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'auction')
                EXEC('CREATE SCHEMA auction');", transaction: (SqlTransaction)transaction);

        await connection.ExecuteAsync(@"
            IF OBJECT_ID('auction.Boxes', 'U') IS NULL
            BEGIN
                CREATE TABLE auction.Boxes (
                    BoxNumber INT NOT NULL PRIMARY KEY,
                    BoxType NVARCHAR(100) NULL,
                    SalesType NVARCHAR(100) NULL,
                    [Group] NVARCHAR(100) NULL,
                    Gender NVARCHAR(100) NULL,
                    Size NVARCHAR(100) NULL,
                    HairLength NVARCHAR(100) NULL,
                    Color NVARCHAR(100) NULL,
                    Quality NVARCHAR(100) NULL,
                    Clarity NVARCHAR(100) NULL,
                    Damages NVARCHAR(100) NULL,
                    Skins INT NOT NULL DEFAULT 0,
                    LastRefreshedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                );
                CREATE INDEX IX_Boxes_BoxType ON auction.Boxes(BoxType);
                CREATE INDEX IX_Boxes_SalesType_Gender_Group ON auction.Boxes(SalesType, Gender, [Group]);
            END", transaction: (SqlTransaction)transaction);

        await connection.ExecuteAsync(
            "DELETE FROM auction.Boxes;",
            transaction: (SqlTransaction)transaction);

        var count = await connection.ExecuteAsync(@"
            INSERT INTO auction.Boxes (BoxNumber, BoxType, SalesType, [Group], Gender, Size, HairLength, Color, Quality, Clarity, Damages, Skins, LastRefreshedAt)
            SELECT
                s.BoxNumber,
                s.BoxType,
                s.SalesType,
                s.[Group],
                s.Gender,
                CAST(s.Size AS NVARCHAR(100)),
                s.HairLength,
                s.Color,
                s.Quality,
                s.Clarity,
                s.Damages,
                COUNT(DISTINCT s.Barcode),
                GETUTCDATE()
            FROM dbo.SkinTable s
            WHERE s.BoxStatus IN ('Showlot', 'Storage')
              AND s.IsActive = 1
            GROUP BY
                s.BoxNumber, s.BoxType, s.SalesType, s.[Group], s.Gender,
                s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages;",
            transaction: (SqlTransaction)transaction);

        transaction.Commit();

        _logger.LogInformation("Refreshed auction.Boxes: {Count} rows", count);
        return count;
    }
}
