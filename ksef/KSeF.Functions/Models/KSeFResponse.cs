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
    /// <summary>Transient failure (network/5xx/timeout) — BC may safely retry. False = terminal, stop retrying.</summary>
    public bool Retryable { get; set; }
    /// <summary>KSeF reports the invoice is already submitted — it IS in KSeF; BC should mark Accepted, never re-send.</summary>
    public bool Duplicate { get; set; }
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
    /// <summary>Transient failure — BC may retry. False = terminal, stop.</summary>
    public bool Retryable { get; set; }
    /// <summary>KSeF reports a duplicate — the invoice is already accepted; BC should mark Accepted.</summary>
    public bool Duplicate { get; set; }
    public string? Error { get; set; }
    public int ProcessingCode { get; set; }
    public string? ProcessingDescription { get; set; }
    public List<string>? Details { get; set; }
    public string? KSeFReferenceNumber { get; set; }
    public DateTime? AcquisitionTimestamp { get; set; }
    public string? QRVerificationUrl { get; set; }
}

/// <summary>
/// A classified KSeF API failure. Carries enough to decide retry vs terminal vs duplicate
/// at the call site instead of substring-matching a generic Exception.message.
/// </summary>
public class KSeFException : Exception
{
    public System.Net.HttpStatusCode? HttpStatus { get; }
    public string? Body { get; }
    /// <summary>True = transient (network/throttling/5xx); a retry may succeed.</summary>
    public bool Retryable { get; }
    /// <summary>True = KSeF says this invoice is already submitted (duplicate).</summary>
    public bool Duplicate { get; }

    public KSeFException(string message, System.Net.HttpStatusCode? httpStatus, string? body, bool retryable, bool duplicate)
        : base(message)
    {
        HttpStatus = httpStatus;
        Body = body;
        Retryable = retryable;
        Duplicate = duplicate;
    }

    /// <summary>
    /// Classify a failed KSeF HTTP response. Duplicate (already submitted) and 4xx client/validation
    /// errors are terminal; 408/429/5xx are transient and worth retrying.
    /// </summary>
    public static KSeFException FromResponse(string context, System.Net.HttpStatusCode status, string? body)
    {
        var b = body ?? string.Empty;
        var code = (int)status;
        var duplicate = code == 409
            || b.Contains("Duplikat", StringComparison.OrdinalIgnoreCase)
            || b.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        var retryable = !duplicate && (code == 408 || code == 429 || code >= 500);
        var truncated = b.Length <= 300 ? b : b[..300] + "...";
        return new KSeFException($"{context}: {status} - {truncated}", status, body, retryable, duplicate);
    }
}
