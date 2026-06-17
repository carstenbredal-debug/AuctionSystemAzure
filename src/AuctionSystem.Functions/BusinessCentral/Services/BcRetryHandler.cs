using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.BusinessCentral.Services;

/// <summary>
/// Retries transient Business Central API failures with exponential backoff + jitter, and logs
/// each retry so flaky BC calls are visible.
///
/// Safety: reads (GET) are retried on network errors, timeouts, 5xx, 429, and BC deadlocks. Writes
/// (POST/PATCH/DELETE) are retried ONLY on 429 (rate limiting, where the request was rejected before
/// processing) — a 5xx/timeout/deadlock after a write may already have applied in BC, so re-sending
/// could duplicate (observed: a deadlocked line-add that had partially applied got added twice on
/// retry, inflating the BC invoice). Deadlocked writes are recovered by the higher-level push: it
/// records the failure and the timer sweep re-pushes the whole document, deleting the stale draft
/// first (DeleteStaleDraft*), which is idempotent — no duplicate lines.
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
                // A BC deadlock comes back under several HTTP codes (seen as 409 Internal_ServerError
                // AND 400 Application_DialogException) with a "deadlocked … Please retry" body. Retry it
                // ONLY for reads — a deadlocked write may have partially applied, so re-sending can
                // duplicate (it did). Deadlocked writes are recovered idempotently by the timer sweep.
                var retriable = status == 429
                    || (status >= 500 && isGet)
                    || ((status == 409 || status == 400 || status == 500) && isGet && await IsBcDeadlockAsync(response, ct));
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
