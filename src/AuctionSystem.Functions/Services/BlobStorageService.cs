using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AuctionSystem.Functions.Services;

public class BlobStorageService
{
    private readonly BlobContainerClient _container;

    public BlobStorageService(string connectionString, string containerName = "invoices")
    {
        var client = new BlobServiceClient(connectionString);
        _container = client.GetBlobContainerClient(containerName);
        _container.CreateIfNotExists(PublicAccessType.Blob);
    }

    public async Task<string> UploadPdfAsync(string fileName, byte[] pdfData)
    {
        var blob = _container.GetBlobClient(fileName);
        using var stream = new MemoryStream(pdfData);
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
        });
        return blob.Uri.ToString();
    }
}
