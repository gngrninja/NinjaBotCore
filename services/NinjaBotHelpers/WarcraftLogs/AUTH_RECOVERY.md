# WCL authentication recovery

Core and Helpers use the same source-linked authentication policy. The canonical files live here so Helpers' existing standalone Docker build context remains valid; Core links them through its project. Each process/client singleton has independent token state and cooldown.

## Behavior

- Read-only API HTTP 401 invalidates **only that request's token generation** and retries once, with new request/content objects. Cold, expired and rejected-token acquisition is single-flight. OAuth failures do not trigger API replay loops.
- A malformed/empty/nonpositive-lifetime token is never published. Expiry uses the injected clock and an expiry margin (up to 60 seconds). A 360-day advertised lifetime cannot prevent recovery from rejection.
- Unsuccessful OAuth or rejection of the replacement token throws `WclAuthenticationUnavailableException` (a sanitized `HttpRequestException`, retaining HTTP status when available). Later requests share a 30-second cooldown. OAuth Retry-After can extend that cooldown to a maximum of five minutes. Caller cancellation does not alter authentication health or impose cooldown; an OAuth request deadline does.
- One HTTP deadline covers token acquisition, gate wait, API call and auth replay. Helpers shares its existing three-transient-retry budget across authentication replay (at most five API wire attempts: initial, three transient retries, one auth replay); it honors Retry-After, disposes abandoned responses, and remains within that same deadline. Core retains its quota/Retry-After gate. 403, 429, 5xx and GraphQL domain errors do not trigger token refresh.
- Responses are capped at 4 MiB **after decompression**, including OAuth. This preserves Core's previous cap and adds the same cap to Helpers. No provider error body, reason phrase, token, credentials or generation identity is logged by the auth policy.
- The live-card refresh coordinator treats typed auth unavailability, including cooldown, as a shared dependency outage: it preserves the card state/failure budget and its normal LastCheckedAt cadence. It does not revive intentionally stopped/deleted/ended cards. Once authentication works, the next due cycle refreshes the existing message; normal raid-end rules still apply.

## Logs and health

Rendered console/file contracts (structured property `Reason` is a fixed code):

```
[WCL Auth] state=unavailable reason=oauth_http
[WCL Auth] state=unavailable reason=oauth_invalid
[WCL Auth] state=unavailable reason=oauth_transport
[WCL Auth] state=unavailable reason=api_rejected
[WCL Auth] state=recovered reason=api_success
```

Unavailable is ERROR: emitted on an unrecovered failure, with at most one reminder per five minutes per client while failures continue. A first 401 followed by successful refresh/replay emits no outage. Recovered is INFORMATION and emitted only after successful authenticated API HTTP status, never on token issuance alone. Late obsolete-token responses cannot clear newer failures. GraphQL/application success and dependency authentication health are different: a 2xx GraphQL error response still demonstrates authenticated transport.

There is no timer heartbeat: an idle process emits neither reminders nor recovery. Logs are not durable state; restarts clear the snapshot and token cache. Existing caller/worker error logs may also appear; alert on these stable auth markers rather than counting arbitrary exceptions as independent outages. This does not configure notification destinations.

Both clients expose immutable `AuthHealth` snapshots: LastApiSuccess, ConsecutiveAuthFailures, LastRefreshSuccess, LastRefreshFailure, NextRetryAt, Unavailable. Failure counts count actual failed refresh/replacement attempts, not suppressed cooldown callers; API success resets the consecutive count. Core adds `warcraft_logs_auth` to the existing authenticated `/api/raid-recap-live/health` JSON without changing existing fields. Helpers does not add a new HTTP health server.

## Operations

On an unavailable alert, check the reason and HTTP status/health timestamps. Verify credentials and provider availability without pasting tokens or provider bodies into logs/chat. Allow the cooldown and next scheduled attempt; do not restart simply to clear cached credentials. Recovery is confirmed by the API-success marker/LastApiSuccess, not by token issuance. Persistent outages require operator action; no retry policy can repair revoked client credentials.

Tests are offline fake HTTP handlers, real client transports/auth policy, real Serilog rendering, and an in-memory live coordinator/caller path. They do not certify live WCL, Discord delivery, production deployment or alert routing.
