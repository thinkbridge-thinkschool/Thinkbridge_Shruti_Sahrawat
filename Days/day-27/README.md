# Day 27 — Security pass

Threat-model the capstone (STRIDE-lite), put the data tier behind private
endpoints, harden the OpenAPI surface, and run a basic pen test.

The work is in three files rather than this one:

| File | What it holds |
|---|---|
| [`threat-model.md`](threat-model.md) | STRIDE-lite over the real code, not a generic checklist — each entry names the file and the line that does or does not mitigate it, including the Development-vs-Production comparison proving the diagnostics gate |
| [`private-endpoints.md`](private-endpoints.md) | What was built, why public access deliberately stays on, the deploy-and-verify output for both environments, and the six environmental failures it took to get there |
| [`zap/`](zap) | The OWASP ZAP baseline and API scans, the scan config, and `probe-surface.ps1` — the endpoint prober whose route names were checked against source rather than guessed |

The API hardening itself is in `QuotesApi/Extensions/ApiSurfaceExtensions.cs`
(versioning, OpenAPI, security headers), `RateLimitingExtensions.cs`, and the
diagnostics gate in `Program.cs`.

## GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-27

## What did you learn this session?

<!-- one line, in your own words -->

## What would break this?

**The data tier is reachable privately, not only privately.** All three private
endpoints exist, resolve, and are Approved — and `publicNetworkAccess` on SQL is
still `Enabled`, because the container app is not VNet-integrated and the public
path is the one it actually uses. So the endpoints are a second route in, not a
replacement for the first. The threat model says this plainly, but it is the
kind of thing that gets summarised as "the database is private" by the second
person to read it. Closing it means VNet-integrating the Container Apps
environment, and the environment is currently borrowed rather than owned
(Days/day-24), so the change is not local to this day.

**A passing scan proves the scanner found nothing it knows to look for, on the
surface it was given.** The API scan reports 0 High, 0 Medium, 1 Low, 4
Informational — against endpoints that answered it with 401. Everything behind
`RequireAuthorization` was, from ZAP's position, a closed door; the scan
therefore says a great deal about the unauthenticated surface and almost nothing
about authorisation logic. Whether a signed-in non-admin can delete another
user's quote is exactly the class of bug a baseline scan cannot see, and it is
the one this application's roles exist to prevent.

**The rate limit is per replica, and prod runs ten of them.** The limiter is an
in-process `FixedWindowRateLimiter` — 300 requests per window globally, 10 on
the auth partition. That is 300 per *instance*. dev caps at two replicas, so the
number on the page is roughly the number in reality; prod caps at ten, so the
real ceiling is ten times the documented one, and it moves whenever the scaler
does. A limit that changes with load is not a limit, and the fix is a shared
store rather than a bigger constant.

**The diagnostics gate depends on an environment variable that a file on disk
can override.** `Diagnostics:Enabled` defaults to `!IsProduction()`, and under
`dotnet run` `launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`
regardless of the shell. That is how the first probe run reported Swagger and
the exception page exposed in "Production" — the probe was right about what it
saw and wrong about what it was talking to. The deployed container sets the
variable explicitly and gates correctly, but the gate is one mis-set variable
away from opening, and nothing fails a deployment that mis-sets it.

**The threat model is accurate on the day it was written.** It cites specific
files and lines, which is what makes it useful and also what makes it decay:
nothing re-runs it, no test asserts that `RequireAuthorization` is still on the
quotes group, and a future refactor that moves an endpoint out from under it
would leave the document confidently describing a mitigation that no longer
exists.
