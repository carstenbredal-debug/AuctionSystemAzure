using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

public class BlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobStorageService> _logger;
    private bool _containerEnsured;

    public BlobStorageService(string connectionString, ILogger<BlobStorageService> logger, string containerName = "invoices")
    {
        _logger = logger;
        var client = new BlobServiceClient(connectionString);
        _container = client.GetBlobContainerClient(containerName);
    }

    private async Task EnsureContainerAsync()
    {
        if (_containerEnsured) return;
        try
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.Blob);
            _containerEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure blob container exists");
        }
    }

    public async Task<string> UploadPdfAsync(string fileName, byte[] pdfData)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(fileName);
        _logger.LogInformation("Uploading PDF {FileName} ({Bytes} bytes) to blob storage", fileName, pdfData.Length);
        using var stream = new MemoryStream(pdfData);
        await blob.UploadAsync(stream, overwrite: true);
        // Set content type after upload
        await blob.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = "application/pdf" });
        var uri = blob.Uri.ToString();
        _logger.LogInformation("PDF uploaded to {Uri}", uri);
        return uri;
    }
}
