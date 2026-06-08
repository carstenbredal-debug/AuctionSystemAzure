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
    {
        _logger = logger;
        _serviceClient = new BlobServiceClient(connectionString);
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
    private async Task<BlobContainerClient> GetPackingContainerAsync()
    {
        if (_packingContainer != null && _packingContainerEnsured) return _packingContainer;
        _packingContainer = _serviceClient.GetBlobContainerClient("packing-orders");
        await _packingContainer.CreateIfNotExistsAsync(PublicAccessType.None);
        _packingContainerEnsured = true;
        return _packingContainer;
    }

    public async Task UploadPackingOrderXmlAsync(string packingOrderNumber, string xmlContent)
    {
        var container = await GetPackingContainerAsync();
        var blobName = $"new/{packingOrderNumber}.xml";
        var blob = container.GetBlobClient(blobName);
        _logger.LogInformation("Uploading packing order XML {BlobName}", blobName);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xmlContent));
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/xml" }
        });
        _logger.LogInformation("Packing order XML uploaded to {Uri}", blob.Uri);
    }

    public async Task<string> UploadPdfAsync(string fileName, byte[] pdfData)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(fileName);
        _logger.LogInformation("Uploading PDF {FileName} ({Bytes} bytes) to blob storage", fileName, pdfData.Length);
        using var stream = new MemoryStream(pdfData);
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
        });

        // Generate a SAS URL valid for 10 years (container may be private)
        string uri;
        if (blob.CanGenerateSasUri)
        {
            var sasBuilder = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddYears(10))
            {
                BlobContainerName = _container.Name,
                BlobName = fileName,
                ContentType = "application/pdf"
            };
            uri = blob.GenerateSasUri(sasBuilder).ToString();
        }
        else
        {
            uri = blob.Uri.ToString();
        }

        _logger.LogInformation("PDF uploaded to {Uri}", uri);
        return uri;
    }
}
