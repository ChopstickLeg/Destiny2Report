# Authenticated queue admission

The public report queue can be protected with Bungie authentication and Redis-backed quotas. Public report reads are unchanged.

Configure the API with environment variables using the standard .NET double-underscore mapping:

| Environment variable | Default | Behavior |
| --- | ---: | --- |
| `QueueAdmission__Enabled` | `false` | Requires a valid Bungie session and enables all admission controls below. |
| `QueueAdmission__MaxRequestsPerAccountPerDay` | `25` | Maximum accepted refreshes and new reports per Bungie account per UTC day. |
| `QueueAdmission__MaxNewReportsPerAccountPerDay` | `5` | Maximum previously unseen reports per Bungie account per UTC day. |
| `QueueAdmission__MaxRequestsGloballyPerHour` | `100` | Maximum accepted queue requests across all accounts per UTC hour. |
| `QueueAdmission__MaxNewReportsGloballyPerDay` | `250` | Maximum previously unseen reports across all accounts per UTC day. |
| `QueueAdmission__BlockedBungieMembershipIds` | empty | Comma-separated Bungie.net membership IDs that may not queue reports. |

A limit of `0` disables that individual limit. Counter checks and increments execute atomically in Redis, so concurrent requests and multiple API instances share the same limits.

When enforcement is enabled, missing or expired sessions fail with `401`, blocked accounts fail with `403`, quota exhaustion fails with `429`, and Bungie or Redis admission failures fail closed with `503`. Turnstile validation and the existing per-report crawl cooldown remain in force.

The API logs `Queue admission identified Bungie account {BungieMembershipId}` for authenticated queue attempts. Use that value in `QueueAdmission__BlockedBungieMembershipIds`; changing environment configuration requires restarting or redeploying the API.

The UI reads `/api/reports/queue-policy`, explains that Bungie sign-in is required on report-generation screens, changes the refresh action to `Sign in to refresh` for signed-out visitors, and skips automatic refresh requests until the visitor is signed in.
