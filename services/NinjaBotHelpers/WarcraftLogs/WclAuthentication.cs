#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#if NINJABOT_HELPERS
namespace NinjaBotHelpers.WarcraftLogs.Auth;
#else
namespace NinjaBot.WarcraftLogs;
#endif

/// <summary>Sanitized dependency failure, not a card-specific failure. No provider exception is retained.</summary>
public sealed class WclAuthenticationUnavailableException : HttpRequestException
{
    public string Reason { get; }
    public DateTimeOffset NextRetryAt { get; }
    internal WclAuthenticationUnavailableException(string reason, HttpStatusCode? status, DateTimeOffset retry)
        : base($"WCL authentication unavailable ({reason}).", null, status)
    { Reason = reason; NextRetryAt = retry; }
}

public sealed record WclAuthHealthSnapshot(DateTimeOffset? LastApiSuccess, long ConsecutiveAuthFailures,
    DateTimeOffset? LastRefreshSuccess, DateTimeOffset? LastRefreshFailure, DateTimeOffset? NextRetryAt,
    bool Unavailable);

internal sealed class WclOAuthFailureException(HttpResponseMessage response, DateTimeOffset now)
    : HttpRequestException($"WCL OAuth failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode)
{
    public TimeSpan RetryDelay { get; } = response.Headers.RetryAfter?.Delta ??
        (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.Zero;
}

/// <summary>One process-local auth policy, source-linked into Core and Helpers. Only read-only API requests may use replay.</summary>
internal sealed class WclAuthentication(TimeProvider clock, ILogger logger)
{
    private sealed class Token(string value, DateTimeOffset expires)
    {
        public string Value { get; } = value;
        public DateTimeOffset Expires { get; } = expires;
    }
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _sync = new();
    private Token? _current;
    private WclAuthHealthSnapshot _health = new(null, 0, null, null, null, false);
    private DateTimeOffset _nextLog;
    private string _reason = "oauth_invalid";
    private HttpStatusCode? _status;
    public WclAuthHealthSnapshot Health { get { lock (_sync) return _health; } }

    public async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<(string Value, long Lifetime)>> fetch,
        Func<string, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct, CancellationToken callerCancellation)
    {
        var token = await GetAsync(fetch, ct, callerCancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await send(token.Value, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                if (response.IsSuccessStatusCode) Success(token);
                return response;
            }
            response.Dispose();
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                // Object identity is the generation fence, even when the provider reissues the same bearer string.
                if (ReferenceEquals(_current, token))
                {
                    _current = null;
                    if (attempt != 0) FailLocked("api_rejected", HttpStatusCode.Unauthorized, refreshFailure: false);
                }
                if (attempt != 0) throw UnavailableLocked();
            }
            token = await GetAsync(fetch, ct, callerCancellation).ConfigureAwait(false);
        }
    }

    private async Task<Token> GetAsync(Func<CancellationToken, Task<(string Value, long Lifetime)>> fetch, CancellationToken ct, CancellationToken callerCancellation)
    {
        // WaitAsync cancellation never owns/releases the gate, nor changes dependency health.
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                if (_health.NextRetryAt is { } retry && clock.GetUtcNow() < retry)
                { RemindLocked(); throw UnavailableLocked(); }
                if (_current is { } cached && clock.GetUtcNow() < cached.Expires) return cached;
                // Retire the expired generation before awaiting OAuth, including a refresh that fails.
                // Its in-flight API responses must not update health or clear the refresh cooldown.
                _current = null;
            }
            try
            {
                var material = await fetch(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var now = clock.GetUtcNow();
                if (string.IsNullOrWhiteSpace(material.Value) || material.Lifetime <= 0 ||
                    material.Lifetime > (DateTimeOffset.MaxValue - now).TotalSeconds ||
                    material.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("Invalid WCL OAuth token.");
                // Short-lived tokens remain usable; don't subtract a full minute from a 30-second token.
                var expiry = now.AddSeconds(material.Lifetime - Math.Min(60, material.Lifetime / 10.0));
                var token = new Token(material.Value, expiry);
                lock (_sync)
                {
                    _current = token;
                    _health = _health with { LastRefreshSuccess = now, NextRetryAt = null };
                }
                return token;
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    FailLocked("oauth_transport", null, refreshFailure: true);
                    throw UnavailableLocked();
                }
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    FailLocked(ex is HttpRequestException http ? http.StatusCode.HasValue ? "oauth_http" : "oauth_transport" : "oauth_invalid",
                        (ex as HttpRequestException)?.StatusCode, refreshFailure: true, (ex as WclOAuthFailureException)?.RetryDelay);
                    throw UnavailableLocked();
                }
            }
        }
        finally { _refresh.Release(); }
    }

    private void FailLocked(string reason, HttpStatusCode? status, bool refreshFailure, TimeSpan? retryDelay = null)
    {
        var now = clock.GetUtcNow();
        _reason = reason; _status = status;
        _health = _health with { Unavailable = true, ConsecutiveAuthFailures = _health.ConsecutiveAuthFailures + 1,
            LastRefreshFailure = refreshFailure ? now : _health.LastRefreshFailure, NextRetryAt = now.AddSeconds(Math.Clamp(retryDelay?.TotalSeconds ?? 30, 30, 300)) };
        RemindLocked();
    }
    private void RemindLocked()
    {
        var now = clock.GetUtcNow();
        if (now < _nextLog) return;
        _nextLog = now.AddMinutes(5);
        logger.LogError("[WCL Auth] state=unavailable reason={Reason}", _reason);
    }
    private WclAuthenticationUnavailableException UnavailableLocked() => new(_reason, _status, _health.NextRetryAt ?? clock.GetUtcNow());
    private void Success(Token token)
    {
        lock (_sync)
        {
            // A delayed response from an obsolete token cannot declare a newer outage recovered.
            if (!ReferenceEquals(_current, token)) return;
            if (_health.Unavailable) logger.LogInformation("[WCL Auth] state=recovered reason=api_success");
            _health = _health with { LastApiSuccess = clock.GetUtcNow(), ConsecutiveAuthFailures = 0, NextRetryAt = null, Unavailable = false };
            _nextLog = DateTimeOffset.MinValue;
        }
    }
}
