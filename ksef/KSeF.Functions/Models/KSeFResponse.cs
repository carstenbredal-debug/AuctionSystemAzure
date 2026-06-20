namespace KSeF.Functions.Models;

/// <summary>
/// Response from KSeF after initiating an interactive session.
/// </summary>
public class KSeFSessionResponse
{
    public string SessionToken { get; set; } = "";
    public string SessionReferenceNumber { get; set; } = "";
    public DateTime? Timestamp { get; set; }
}

/// <summary>
/// Response from KSeF after sending an invoice.
/// </summary>
public class KSeFSendResponse
{
    public string ElementReferenceNumber { get; set; } = "";
    public DateTime? ProcessingTimestamp { get; set; }
}

/// <summary>
/// Status of a submitted invoice in KSeF.
/// </summary>
public class KSeFInvoiceStatus
{
    public string ElementReferenceNumber { get; set; } = "";
    public int ProcessingCode { get; set; }
    public string? ProcessingDescription { get; set; }
    public List<string>? Details { get; set; }
    public string? KSeFReferenceNumber { get; set; }
    public DateTime? AcquisitionTimestamp { get; set; }
}

/// <summary>
/// UPO (Urzędowe Poświadczenie Odbioru) — official acknowledgment.
/// </summary>
public class KSeFUpo
{
    public string UpoReferenceNumber { get; set; } = "";
    public byte[]? UpoData { get; set; }
    public string? ContentType { get; set; }
}

/// <summary>
/// Result returned to Business Central after invoice submission.
/// </summary>
public class SubmitResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ElementReferenceNumber { get; set; }
    public string? KSeFReferenceNumber { get; set; }
    public string? SessionToken { get; set; }
    public string? SessionReferenceNumber { get; set; }
    public string? QRVerificationUrl { get; set; }
}

/// <summary>
/// Result returned when checking invoice status.
/// </summary>
public class StatusResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int ProcessingCode { get; set; }
    public string? ProcessingDescription { get; set; }
    public List<string>? Details { get; set; }
    public string? KSeFReferenceNumber { get; set; }
    public DateTime? AcquisitionTimestamp { get; set; }
    public string? QRVerificationUrl { get; set; }
}
