using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class TargetCatalogDbOptions
{
    public string ConnectionString { get; set; } = "";
}

public class CatalogCopyFunctions
{
    private readonly CatalogDbContext _catalogDb;
    private readonly TargetCatalogDbOptions _targetOptions;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CatalogCopyFunctions(CatalogDbContext catalogDb, TargetCatalogDbOptions targetOptions)
    {
        _catalogDb = catalogDb;
        _targetOptions = targetOptions;
    }

    [Function("AppendCatalog")]
    public async Task<HttpResponseData> AppendCatalog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog-copy/append")] HttpRequestData req)
    {
        if (string.IsNullOrEmpty(_targetOptions.ConnectionString))
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            err.Headers.Add("Content-Type", "application/json");
            await err.WriteStringAsync(JsonSerializer.Serialize(new { error = "TargetCatalogConnectionString is not configured" }, JsonOptions));
            return err;
        }

        var rows = await CopyCatalogToTarget(truncateFirst: false);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, action = "append", rowsCopied = rows }, JsonOptions));
        return response;
    }

    [Function("RecreateCatalog")]
    public async Task<HttpResponseData> RecreateCatalog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog-copy/recreate")] HttpRequestData req)
    {
        if (string.IsNullOrEmpty(_targetOptions.ConnectionString))
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            err.Headers.Add("Content-Type", "application/json");
            await err.WriteStringAsync(JsonSerializer.Serialize(new { error = "TargetCatalogConnectionString is not configured" }, JsonOptions));
            return err;
        }

        var rows = await CopyCatalogToTarget(truncateFirst: true);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, action = "recreate", rowsCopied = rows }, JsonOptions));
        return response;
    }

    private async Task<int> CopyCatalogToTarget(bool truncateFirst)
    {
        // Read all catalog lots from source
        var lots = await _catalogDb.CatalogLots.AsNoTracking().ToListAsync();

        await using var conn = new SqlConnection(_targetOptions.ConnectionString);
        await conn.OpenAsync();

        if (truncateFirst)
        {
            await using var truncCmd = new SqlCommand("TRUNCATE TABLE dbo.ForAuctionHub", conn);
            await truncCmd.ExecuteNonQueryAsync();
        }

        // Bulk insert using SqlBulkCopy for performance
        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.ForAuctionHub",
            BatchSize = 1000
        };

        bulkCopy.ColumnMappings.Add("LotNumber", "LotNumber");
        bulkCopy.ColumnMappings.Add("StringNumber", "StringNumber");
        bulkCopy.ColumnMappings.Add("CatalogSortOrder", "CatalogSortOrder");
        bulkCopy.ColumnMappings.Add("IsShow", "IsShow");
        bulkCopy.ColumnMappings.Add("SalesType", "SalesType");
        bulkCopy.ColumnMappings.Add("Gender", "Gender");
        bulkCopy.ColumnMappings.Add("Group", "Group");
        bulkCopy.ColumnMappings.Add("HairLength", "HairLength");
        bulkCopy.ColumnMappings.Add("Size", "Size");
        bulkCopy.ColumnMappings.Add("Quality", "Quality");
        bulkCopy.ColumnMappings.Add("Color", "Color");
        bulkCopy.ColumnMappings.Add("Clarity", "Clarity");
        bulkCopy.ColumnMappings.Add("Damages", "Damages");
        bulkCopy.ColumnMappings.Add("IncludedBoxNumbers", "IncludedBoxNumbers");
        bulkCopy.ColumnMappings.Add("BoxCount", "BoxCount");
        bulkCopy.ColumnMappings.Add("TotalSkins", "TotalSkins");

        var table = new System.Data.DataTable();
        table.Columns.Add("LotNumber", typeof(int));
        table.Columns.Add("StringNumber", typeof(int));
        table.Columns.Add("CatalogSortOrder", typeof(int));
        table.Columns.Add("IsShow", typeof(string));
        table.Columns.Add("SalesType", typeof(string));
        table.Columns.Add("Gender", typeof(string));
        table.Columns.Add("Group", typeof(string));
        table.Columns.Add("HairLength", typeof(string));
        table.Columns.Add("Size", typeof(string));
        table.Columns.Add("Quality", typeof(string));
        table.Columns.Add("Color", typeof(string));
        table.Columns.Add("Clarity", typeof(string));
        table.Columns.Add("Damages", typeof(string));
        table.Columns.Add("IncludedBoxNumbers", typeof(string));
        table.Columns.Add("BoxCount", typeof(int));
        table.Columns.Add("TotalSkins", typeof(int));

        foreach (var lot in lots)
        {
            table.Rows.Add(
                lot.LotNumber,
                lot.StringNumber,
                lot.CatalogSortOrder,
                lot.IsShow ?? "",
                (object?)lot.SalesType ?? DBNull.Value,
                (object?)lot.Gender ?? DBNull.Value,
                (object?)lot.Group ?? DBNull.Value,
                (object?)lot.HairLength ?? DBNull.Value,
                (object?)lot.Size ?? DBNull.Value,
                (object?)lot.Quality ?? DBNull.Value,
                (object?)lot.Color ?? DBNull.Value,
                (object?)lot.Clarity ?? DBNull.Value,
                (object?)lot.Damages ?? DBNull.Value,
                (object?)lot.IncludedBoxNumbers ?? DBNull.Value,
                lot.BoxCount,
                lot.TotalSkins
            );
        }

        await bulkCopy.WriteToServerAsync(table);
        return lots.Count;
    }
}
