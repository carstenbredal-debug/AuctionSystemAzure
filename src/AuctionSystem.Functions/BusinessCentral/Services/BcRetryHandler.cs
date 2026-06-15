using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.BusinessCentral.Services;

/// <summary>
/// Retries transient Business Central API failures with exponential backoff + jitter, and logs
/// each retry so flaky BC calls are visible.
///
/// Safety: reads (GET) are retried on network errors, timeouts, 5xx and 429. Writes
/// (POST/PATCH/DELETE) are retried ONLY on 429 (rate limiting, where the request was rejected
/// before processing) — a 5xx/timeout after a write may already have been applied in BC, so
/// re-sending could duplicate. Recovering those is left to the higher-level idempotency/claim
/// guards in BusinessCentralSyncService.
/// </summary>
public sealed class BcRetryHandler : DelegatingHandler
{
    private const int MaxRetries = 3;
    private readonly ILogger<BcRetryHandler> _logger;

    public BcRetryHandler(ILogger<BcRetryHandler> logger) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer the body once so the request can be re-sent (an HttpRequestMessage is single-use).
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var isGet = request.Method == HttpMethod.Get;

        for (var attempt = 0; ; attempt++)
        {
            using var attemptReq = Clone(request, body);
            try
            {
                var response = await base.SendAsync(attemptReq, ct);
                var retriable = (int)response.StatusCode == 429 || ((int)response.StatusCode >= 500 && isGet);
                if (!retriable || attempt >= MaxRetries)
                    return response;

                var delay = RetryAfter(response) ?? Backoff(attempt);
                _logger.LogWarning("BC {Method} {Uri} returned {Status}; retry {Attempt}/{Max} in {Delay}ms",
                    request.Method, request.RequestUri, (int)response.StatusCode, attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (isGet && attempt < MaxRetries
                                       && ex is HttpRequestException or TaskCanceledException
                                       && !ct.IsCancellationRequested)
            {
                var delay = Backoff(attempt);
                _logger.LogWarning(ex, "BC {Method} {Uri} failed transiently; retry {Attempt}/{Max} in {Delay}ms",
                    request.Method, request.RequestUri, attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage req, byte[]? body)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };
        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (req.Content is not null)
                foreach (var h in req.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 250));

    private static TimeSpan? RetryAfter(HttpResponseMessage r) =>
        r.Headers.RetryAfter?.Delta
        ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null);
}
