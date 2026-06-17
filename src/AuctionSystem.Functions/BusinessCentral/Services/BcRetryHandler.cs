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
///
/// Exception: a BC SQL deadlock surfaces as 409 Conflict with "deadlocked … Please retry the
/// activity". BC kills one transaction as the deadlock victim and FULLY ROLLS IT BACK, so the
/// write did not apply — making it safe to retry even for writes. We detect it by body and retry.
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
                var status = (int)response.StatusCode;
                var retriable = status == 429
                    || (status >= 500 && isGet)
                    || (status == 409 && await IsBcDeadlockAsync(response, ct)); // rolled-back victim, safe to retry
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

    // A 409 is a BC deadlock only if the body says so. Buffer the content first so that, if we decide
    // NOT to retry, the caller (EnsureSuccessAsync) can still read the same body.
    private static async Task<bool> IsBcDeadlockAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null) return false;
        await response.Content.LoadIntoBufferAsync();
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Please retry the activity", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 250));

    private static TimeSpan? RetryAfter(HttpResponseMessage r) =>
        r.Headers.RetryAfter?.Delta
        ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null);
}
