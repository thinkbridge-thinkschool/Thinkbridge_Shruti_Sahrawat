[← Back to the day index](../README.md)

## Day 32 — Ship + demo + postmortem

Ship it live, demo it, and write a one-page postmortem: what you'd do
differently, what the hardest bug taught you, and the one thing you're proudest
of.

The last day, and the one that found the worst defect in the repository.

---

### What changed

| File | What it does now |
|---|---|
| `QuotesApi/Controllers/CollectionsController.cs` | `[Authorize]` on the class. Seven endpoints were anonymous, on the public internet |
| `Quotes.Tests.Integration/CollectionsEndpointsTests.cs` | Two tests: anonymous is refused on every verb, and the ownership gap that is still open |
| `QuotesApi/QuotesApi.csproj` | `ContainerImageTag` 0.3.0 — source now records the image it produces |
| `POSTMORTEM.md` | New. One page, the three questions |
| `DEMO.md` | New. Captured output, both halves |
| `STATUS.md` | New. What is finished, what is scaffolding, what was never built |
| `DEPLOYMENT.md` | New. The deployment that actually exists |
| `DEPLOY-RUNBOOK.md`, `VERIFICATION-DEPLOY.md` | Banner: historical, describes a decommissioned subscription |
| `azure.yaml` | The `resources` block removed — it pinned a dead SQL server and would have been applied by `azd up` |
| `capstone/Capstone.slnx` | New. The capstone builds on its own, 14 projects instead of 22 |
| `capstone/demo/walkthrough.ps1` | New. The slice, re-runnable |
| `next-steps.md` | Deleted. 65 lines of `azd init` boilerplate nothing referenced |

---

### The find

`CollectionsController` had no authorization. Not on the class, not on any of
its seven actions, and `AddAuthorization()` carries no fallback policy. So
every collections endpoint was anonymous — including
`DELETE /api/collections/{id}/items/{quoteId}` and a `GET` that returns every
collection in the database — and reachable on the public internet through the
Static Web App front door.

```
$ curl -i https://black-sea-0f5ad2a00.5.azurestaticapps.net/api/collections
HTTP/1.1 200 OK
Content-Length: 2
...
[]

$ curl -o /dev/null -w "%{http_code}" .../api/quotes
401
```

Same app, same deployment, same request shape. The quotes group is protected
and the controller is not, and that contrast is the diagnosis rather than a
curiosity: day 25 put `RequireAuthorization()` on the *group*, so anything
added to that group is protected without anyone remembering. The controller is
the one endpoint surface in the app registered a different way, and it
inherited none of that decision.

It returned `[]` rather than data only because that database holds no
collections. Luck, not protection.

**Two earlier passes should have caught it.** Day 27's security pass walked the
endpoint groups. Day 31's re-check walked the capstone. Neither walked the one
file that was neither.

---

### Findings

**1 — A test suite where every caller is authenticated cannot find an
unauthenticated endpoint.** All eleven existing collections tests use
`host.Client`, which `CreateFreshHost` signs in before handing it over. That is
convenient, it is why they are readable, and it is precisely why the hole
survived: a suite in which nobody is ever anonymous has no way to notice that
anonymity is permitted. `[Authorize]` broke none of those eleven tests — which
is the same fact stated the other way round.

The fix is asserted across every verb rather than on one endpoint, because
`[Authorize]` on the class and `[Authorize]` on one action pass a
single-endpoint test identically, and only one of them protects the eighth
action somebody adds later.

**2 — Proven before it was fixed, and not in production.** The tests landed
first and went red against a throwaway SQL Server container:

```
Expected (anonymous.GetAsync("/api/collections")).StatusCode to be
HttpStatusCode.Unauthorized {value: 401} because GET / returns every collection
in the database, but found HttpStatusCode.OK {value: 200}.
```

The alternative — `POST`ing a collection to the live database to demonstrate the
write path — would have proven the same thing and left evidence of the
demonstration in production data. The read returning 200 already establishes
that the authorization filter is absent; the write follows from the same missing
attribute.

**3 — Healthy and 100% traffic does not mean the change shipped.** After
`az containerapp update`, the revision reported `Healthy` with all traffic. That
says the container started. The check that mattered was
`GET /api/collections` going from 200 to 401 on the public URL, because that was
the defect. A revision can be perfectly healthy and be the old image.

**4 — Two wrong findings, caught before they were written down.** Both worth
recording, because the second one is the more instructive.

The first: the API's own hostname returns 401 on `/health`, and I began writing
this up as a broken health probe. `az containerapp auth show` said otherwise —
the Container App is a *linked backend* of the Static Web App, which enables
Easy Auth on it with the SWA as the identity provider so that the front door is
the only way in. `x-ms-middleware-request-id` in the response headers was the
tell all along: the sidecar was answering, not the app.

The second: thirty `Jwt:Key was not configured. Generated an ephemeral key`
warnings went past in the test run, and an ephemeral signing key in production
would mean every restart silently invalidates every issued token. The deployed
environment has `Jwt__Key` set. The warning is local-only. Checking cost one
command; writing it up would have cost credibility.

**5 — A stale config file is not inert.** The root `azure.yaml` pinned
`ConnectionStrings__Default` to `sql-quotes2-qvdk5l.database.windows.net` and a
managed-identity client id from a subscription that no longer exists. Harmless
for weeks because nobody ran `azd`, and a live outage the moment somebody did —
`azd up` would have applied those values over a working configuration while
appearing to deploy. The `resources` block is gone and
[DEPLOYMENT.md](../../DEPLOYMENT.md) is the current truth.

The two historical runbooks were *not* corrected. They are the evidence for days
24 and 25, and editing them to name today's resources would misrepresent what
was true then. They carry a banner instead.

**6 — The capstone had no solution file of its own.** It existed only inside
the 22-project root solution, which is why a capstone-only change built
QuotesApi, OrderRefactor and Quotes.Worker and printed 53 warnings.
`capstone/Capstone.slnx` has the 14 projects that belong to it.

---

### Proof

The security fix, shipped:

| public URL | 05:05 | 05:40 |
|---|---:|---:|
| `/api/collections` | **200** | **401** |
| `/api/collections/summaries` | **200** | **401** |
| `/api/quotes` | 401 | 401 |
| `/` | 200 | 200 |

Revision `quotes-api-dev--0000005`, image `quotes-api:0.3.0`, Healthy, 100%
traffic.

```
$ dotnet test Quotes.Tests.Integration --configuration Release
Test summary: total: 56, failed: 0, succeeded: 56, skipped: 0, duration: 120.1s
```

The capstone slice, one row in two states — full transcript in
[DEMO.md](../../DEMO.md):

```
== 6. the outbox, immediately
{ "messageId": "01a0b2fe-2ed4-7c9f-973b-e079891e1e02",
  "occurredAt": "2026-09-18T05:29:57.7095178+00:00",
  "sentAt": null, "delivered": false }

== 9. the same row, acknowledged
{ "messageId": "01a0b2fe-2ed4-7c9f-973b-e079891e1e02",
  "occurredAt": "2026-09-18T05:29:57.7095178+00:00",
  "sentAt": "2026-09-18T05:30:03.7674999+00:00", "delivered": true }
```

Same id, same `occurredAt`, nothing deleted. 6.06 seconds here and 466ms on the
previous run of the identical script — the delay is wherever the publish lands
in the poll cycle, which is why subscribers are promised nothing about it.

---

### The postmortem

[POSTMORTEM.md](../../POSTMORTEM.md) — one page: what I would do differently,
what the hardest bug taught me, the one thing I am proudest of.

The short version of the first: make security structural instead of remembered.
Three instances of one mistake — this controller's `ownerId`, the capstone's
`curatorId`, and this controller having no `[Authorize]` at all — is not three
mistakes. It is a missing default.

---

### What did you learn this session?

A green test suite where every caller carries a token cannot tell a protected
endpoint from an unprotected one — the 56 tests that passed after the fix are
the same 56 that passed while a DELETE was anonymous on the internet.

### What would break this?

The next endpoint added outside a `RequireAuthorization()` group: there is still
no authorization fallback policy, so protection remains something somebody has
to remember rather than something the framework refuses to omit.

### GitHub link

[`Days/day-32/`](https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-32)
