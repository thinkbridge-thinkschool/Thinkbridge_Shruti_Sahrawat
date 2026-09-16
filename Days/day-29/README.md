[← Back to the day index](../README.md)

## Day 29 — Build day 1: foundation + happy path

Build the foundation and the happy path end to end. Small, reviewable commits
with clean messages; the main flow works against real infrastructure by end of
day.

This is day 1 of the six-day build plan [Day 28](../day-28/README.md) wrote,
and that plan already named it: **EF Core persistence for the Curation
module**. The scaffold from the capstone kickoff stored aggregates in a
`Dictionary<CollectionId, Collection>`; by the end of today the same publish
walkthrough runs against a real relational database, through real migrations,
in a real transaction.

---

### What changed

| File | What it does now |
|---|---|
| `Capstone.Curation.Infrastructure/CurationDbContext.cs` | New. `Collection` mapped as an entity, `Items` as an owned collection, every identifier through a value converter |
| `Capstone.Curation.Infrastructure/CollectionRepository.cs` | New. Replaces `InMemoryCollectionRepository` — loads and adds through the DbContext |
| `Capstone.Curation.Infrastructure/UnitOfWork.cs` | Drains domain events from EF's own change tracker, then `SaveChangesAsync` |
| `Capstone.Api/Program.cs` | `AddDbContext`, a configurable provider, and Curation's services moved from singleton to scoped |
| `Capstone.Api/appsettings.json` | New. Provider and connection string |
| `Migrations/…_InitialCreate.cs` | The `Collections` and `CollectionItems` tables |

`Capstone.Curation.Domain` is untouched, and that is the point rather than a
coincidence. Its twenty tests pass without a single edit, and
`DeclaredReferenceTests` still passes, which is what proves the domain did not
quietly acquire a persistence dependency on the day persistence arrived.

### The one mapping line that carries the most weight

```csharp
collection.Property(c => c.Id)
    .HasConversion(id => id.Value, value => new CollectionId(value))
    .ValueGeneratedNever();
```

`ValueGeneratedNever` is load-bearing. EF's default convention for a `Guid`
key is that the store generates it, and under that convention EF would
overwrite the id the aggregate already minted. `CollectionId`'s own remarks
explain why the domain mints it: an identity that exists before `SaveChanges`
is what lets the state change and the outbox row commit in one transaction,
instead of the two-save dance Day 20 paid for in `QuotesApi`. Leave this line
out and nothing fails loudly — the insert succeeds, with a different id than
the one the handler returned to the caller.

The walkthrough below shows the id coming back intact, version field and all.

### Findings

**1 — A generated migration is not an applied migration.** The first run of
the happy path answered `500`. The migration had been created and never run,
so `SaveChangesAsync` addressed a database that did not exist. `migrations
add` writes C# describing a schema; `database update` is what makes the schema
real. Two commands, and only the second one touches a database.

**2 — `Microsoft.EntityFrameworkCore.Design` has to be on the startup
project.** It was referenced from `Capstone.Curation.Infrastructure`, where
the DbContext and the migrations live, with `PrivateAssets=all` — correct,
because a design-time tool should not become a transitive dependency of
everything downstream. That correctness is exactly why it did not satisfy the
tooling: `dotnet ef` builds and inspects the **startup** project, so the
package has to be visible from `Capstone.Api` too. The package is in the right
place for building and the wrong place for tooling, and those are different
places.

**3 — The default provider was chosen on a false assumption.** SQL Server
LocalDB was assumed present and is not installed on this machine:
`error: 52 - Unable to locate a Local Database Runtime installation`. The fix
is the pattern `QuotesApi` already uses — reference both providers, pick from
configuration — with SQLite as the default so a fresh clone runs with no
database engine installed at all. "It works on the machine it was written on"
is not a property a template should depend on.

**4 — Migrations are not portable across providers.** The `InitialCreate`
generated against SQL Server had to be removed and regenerated once the
provider changed, because a migration is emitted against one provider's type
mappings. This is [Day 24](../day-24/README.md) Finding 17 met from the
opposite direction: there, every `QuotesApi` migration had been generated
against SQLite and therefore could never be applied to Azure SQL, which is why
that database runs `EnsureCreated` instead of `Migrate`. Same wall, same day
one. Day 6 of the build plan is where it has to be answered properly, and the
answer is provider-specific migration assemblies rather than one folder that
silently belongs to whichever provider was configured last.

**5 — The convenience arrived carrying a vulnerability.** Adding the SQLite
provider introduced `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 transitively, which
carries a known high-severity advisory (`NU1903`, GHSA-2m69-gcr7-jv3q). It is
a warning, the build is green, and it would scroll past unread on any busy
day. Recorded here rather than left in the terminal, because
[Day 27](../day-27/README.md) spent a whole day on the principle that a
security finding nobody writes down is a security finding nobody has.

**6 — `Capstone.Api` starts in Production.** `dotnet run` reports
`Hosting environment: Production`, because this project has no
`launchSettings.json` and nothing sets `ASPNETCORE_ENVIRONMENT`. So a failing
request returns a bare 500 with no detail in the body and the reason is only
in the console. That is the mirror image of Day 27's diagnostics-gate finding:
there, `launchSettings.json` forced Development when Production was wanted;
here the absence of one gives Production when Development was wanted. Both
are the same lesson — the environment a process runs in is decided by files
and variables that are easy to not know about.

### The commit log for the day

```
$ git log --oneline 624ea36..HEAD

1bab0c1  Merge remote-tracking branch 'origin/main' into dev
a468bdc  Write up Day 29 - build day 1, EF Core persistence for Curation
09fbbce  Add the InitialCreate migration for Collections and CollectionItems
136b7f1  Give the Curation aggregate real EF Core persistence
```

Three commits of work and one merge, in the order the exercise asks for them:
the mapping and the wiring first, the generated migration on its own so the
hand-written change and the tool-written one can be reviewed separately, then
the write-up. Opened as [PR #5](https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/pull/5).

### Proof

```
$ dotnet ef database update --project src\Modules\Curation\Capstone.Curation.Infrastructure --startup-project src\Capstone.Api
Applying migration '20260915102426_InitialCreate'.
      CREATE TABLE "Collections" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_Collections" PRIMARY KEY,
          "CuratorId" TEXT NOT NULL,
          "Name" TEXT NOT NULL,
          "Status" TEXT NOT NULL,
          "PublishedAt" TEXT NULL
      );
      CREATE TABLE "CollectionItems" (
          "Id" INTEGER NOT NULL CONSTRAINT "PK_CollectionItems" PRIMARY KEY AUTOINCREMENT,
          "QuoteId" INTEGER NOT NULL,
          "AddedAt" TEXT NOT NULL,
          "CollectionId" TEXT NOT NULL,
          CONSTRAINT "FK_CollectionItems_Collections_CollectionId" FOREIGN KEY ("CollectionId") REFERENCES "Collections" ("Id") ON DELETE CASCADE
      );
Done.
```

```
$ dotnet test tests\Capstone.Curation.Domain.Tests
Test summary: total: 20, failed: 0, succeeded: 20, skipped: 0

$ dotnet test tests\Capstone.ArchitectureTests
Test summary: total: 6, failed: 0, succeeded: 6, skipped: 0
```

The happy path, against the database rather than a dictionary. Every response
piped through `ConvertTo-Json`, because PowerShell renders a sequence of
differently-shaped objects under the first one's table header and silently
shows the rest as blank rows - which is how a walkthrough ends up "proving"
values nobody actually saw:

```powershell
PS> $body = @{ curatorId = "alice"; name = "Distributed Systems Wisdom" } | ConvertTo-Json
PS> $c = Invoke-RestMethod -Method Post http://localhost:5000/api/collections `
         -Body $body -ContentType application/json
PS> $c | ConvertTo-Json
{
    "collectionId":  "01a0a8f3-4a57-7d56-b197-0f3f7cf94676"
}

PS> Invoke-RestMethod -Method Post "http://localhost:5000/api/collections/$($c.collectionId)/items" `
         -Body '{"quoteId":1}' -ContentType application/json | ConvertTo-Json
{
    "items":  1
}

PS> Invoke-RestMethod -Method Post "http://localhost:5000/api/collections/$($c.collectionId)/publish" `
         -Body '{"curatorId":"alice"}' -ContentType application/json | ConvertTo-Json
{
    "published":  true
}
```

The id is worth one more look: `01a0a8f3-4a57-**7**d56-...`. The `7` opening
the third group is the UUID version field, so the Guid v7 the domain minted
is the one the database stored and the one the caller got back - three places
that would disagree if `ValueGeneratedNever` were missing from the mapping.

### The proof that it is persistence and not memory

Everything above would look identical against the dictionary this day
replaced. This is the part that would not: the API process is killed, a new
one is started, and the *same* collection is published again.

```powershell
PS> Get-Process -Name Capstone.Api | Stop-Process
PS> dotnet run --project src\Capstone.Api      # a different process, empty memory

PS> try {
      Invoke-RestMethod -Method Post "http://localhost:5000/api/collections/01a0a8f3-4a57-7d56-b197-0f3f7cf94676/publish" `
        -Body '{"curatorId":"alice"}' -ContentType application/json
    } catch {
      "HTTP " + $_.Exception.Response.StatusCode.value__
      [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream()).ReadToEnd()
    }

HTTP 400
{"error":"This collection is already published."}
```

Two things are true at once in that response, and only persistence makes both
true. The row outlived the process that wrote it - against the old in-memory
store the answer would have been "Collection ... was not found", because the
dictionary died with the host. And the error is
`Collection.Publish`'s own invariant, raised against a `Status` and an item
list that EF materialised from SQLite rows through the value converters,
which means the aggregate reconstituted from the database is the same
aggregate that enforced the rule before it was ever saved.

The `try/catch` is not decoration: `Invoke-RestMethod` throws on a 4xx rather
than returning the body, so without it the message the endpoint deliberately
returns is the one thing you cannot see.

### What did you learn this session?

That the interesting part of adding persistence was not the mapping - it was
the four separate ways the environment around a correct mapping can be wrong,
none of which the code could see.

### What would break this?

**The outbox is staged before the state is saved.** `UnitOfWork.CommitAsync`
enqueues the translated event and then calls `SaveChangesAsync`, so a commit
that throws leaves an announcement staged for a state change that never
happened. Inline and in-memory that is survivable; it stops being survivable
the moment the relay is a separate process reading a real table. That is day 2
of the plan, and it is the first thing that would be wrong to ship.

**One migrations folder, two providers.** `Migrations/` now belongs to SQLite
because SQLite generated it last. Nothing in the build says so, nothing fails
if someone flips `Database:Provider` to SqlServer, and the failure when it
finally comes is at `database update` against a real environment rather than
at compile time on a laptop.

### GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-29
