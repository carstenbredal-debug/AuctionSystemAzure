using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

public class BlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly BlobServiceClient _serviceClient;
    private readonly ILogger<BlobStorageService> _logger;
    private bool _containerEnsured;
    private bool _packingContainerEnsured;

    public BlobStorageService(string connectionString, ILogger<BlobStorageService> logger, string containerName = "invoices")
        : this(new BlobServiceClient(connectionString), logger, containerName) { }

    // Identity path (PROD/TEST in Azure): a BlobServiceClient built with the func's managed identity.
    public BlobStorageService(BlobServiceClient serviceClient, ILogger<BlobStorageService> logger, string containerName = "invoices")
    {
        _logger = logger;
        _serviceClient = serviceClient;
        _container = _serviceClient.GetBlobContainerClient(containerName);
    }

    private async Task EnsureContainerAsync()
    {
        if (_containerEnsured) return;
        try
        {
            // Try private access first (public access may be disabled on the storage account)
            await _container.CreateIfNotExistsAsync(PublicAccessType.None);
            _containerEnsured = true;
            _logger.LogInformation("Blob container '{Container}' ensured", _container.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure blob container '{Container}' exists", _container.Name);
            throw;
        }
    }

    private BlobContainerClient? _packingContainer;
    public async Task<BlobContainerClient> GetPackingContainerAsync()
    {
        if (_packingContainer != null && _packingContainerEnsured) return _packingContainer;
        _packingContainer = _serviceClient.GetBlobContainerClient("packing-orders");
        await _packingContainer.CreateIfNotExistsAsync(PublicAccessType.None);
        _packingContainerEnsured = true;
        return _packingContainer;
    }

    public async Task UploadPackingOrderXmlAsync(string packingOrderNumber, string xmlContent)
    {
        await UploadPackingXmlAsync($"new/{packingOrderNumber}.xml", xmlContent);
    }

    public async Task UploadPackingXmlAsync(string blobPath, string xmlContent)
    {
        var container = await GetPackingContainerAsync();
        var blob = container.GetBlobClient(blobPath);
        _logger.LogInformation("Uploading packing XML {BlobName}", blobPath);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xmlContent));
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/xml" }
        });
        _logger.LogInformation("Packing XML uploaded to {Uri}", blob.Uri);
    }

    public async Task DeletePackingOrderXmlsAsync(string packingOrderNumber)
    {
        var container = await GetPackingContainerAsync();
        foreach (var folder in new[] { "new", "processed" })
        {
            var blobClient = container.GetBlobClient($"{folder}/{packingOrderNumber}.xml");
            await blobClient.DeleteIfExistsAsync();
            _logger.LogInformation("Deleted packing XML {Folder}/{Number}.xml (if existed)", folder, packingOrderNumber);
        }
    }

    public async Task<List<(string FileName, string Folder, string Content)>> ListPackingOrderXmlsAsync()
    {
        var container = await GetPackingContainerAsync();
        var results = new List<(string FileName, string Folder, string Content)>();

        foreach (var folder in new[] { "new", "processed" })
        {
            await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{folder}/", default))
            {
                if (!blob.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var blobClient = container.GetBlobClient(blob.Name);
                var download = await blobClient.DownloadContentAsync();
                var content = download.Value.Content.ToString();
                var fileName = blob.Name.Split('/').Last();
                results.Add((fileName, folder, content));
            }
        }

        return results;
    }

    public async Task<int> ClearAllPackingOrderXmlsAsync()
    {
        var container = await GetPackingContainerAsync();
        int deleted = 0;
        foreach (var folder in new[] { "new", "processed" })
        {
            await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{folder}/", default))
            {
                await container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
                deleted++;
            }
        }
        return deleted;
    }

    public Task<string> UploadPdfAsync(string fileName, byte[] pdfData)
        => UploadFileAsync(fileName, pdfData, "application/pdf");

    public async Task<string> UploadFileAsync(string fileName, byte[] data, string contentType)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(fileName);
        _logger.LogInformation("Uploading {FileName} ({Bytes} bytes, {ContentType}) to blob storage", fileName, data.Length, contentType);
        using var stream = new MemoryStream(data);
        await blob.UploadAsync(stream, overwrite: true);
        await blob.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = contentType });

        // Generate a SAS URL valid for 10 years (container may be private)
        string uri;
        if (blob.CanGenerateSasUri)
        {
            var sasBuilder = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddYears(10))
            {
                BlobContainerName = _container.Name,
                BlobName = fileName,
                ContentType = contentType
            };
            uri = blob.GenerateSasUri(sasBuilder).ToString();
        }
        else
        {
            uri = blob.Uri.ToString();
        }

        _logger.LogInformation("File uploaded to {Uri}", uri);
        return uri;
    }
}
