# The capstone slice, end to end, as a walkthrough somebody can re-run.
#
# Start the API first, with a deliberately slow relay poll:
#
#   cd capstone/src/Capstone.Api
#   Remove-Item capstone-curation.db*
#   dotnet ef database update --project ..\Modules\Curation\Capstone.Curation.Infrastructure --startup-project .
#   $env:Relay__PollIntervalMilliseconds="10000"
#   dotnet run --configuration Release
#
# Then run this from anywhere:
#
#   .\capstone\demo\walkthrough.ps1
#
# Why a ten-second poll instead of the default 250ms. The single most important
# thing this walkthrough has to show is one row in two states: committed and
# not yet delivered, then the same row acknowledged. At 250ms that transition
# is unobservable by hand - Day 30 only caught it by accident, inside a 325ms
# window, and the README had to admit as much. Slowing the poll turns the
# design's central claim from something you take on trust into something you
# watch happen. The poll interval became configuration on Day 31 so the tests
# could park the relay; this is the second thing that bought.

param(
    [string]$BaseUrl = "http://localhost:5000",
    [int]$RelayWaitSeconds = 12
)

$ErrorActionPreference = "Stop"
$json = "application/json"

# Every response goes through ConvertTo-Json, and that is not decoration.
# PowerShell renders a sequence of differently-shaped objects under the first
# one's table header and shows the rest as blank rows - which on Day 29 put two
# values into a README that were never on screen. An explicit serialisation is
# the fix, and the reason it is a rule here rather than a habit.
function Show-Step([string]$Label, $Response) {
    Write-Host ""
    Write-Host "== $Label" -ForegroundColor Cyan

    $items = @($Response)

    if ($items.Count -eq 0) {
        Write-Host "[]  (empty)"
        return
    }

    $items | ConvertTo-Json -Depth 6
}

Write-Host "Capstone walkthrough against $BaseUrl" -ForegroundColor Yellow

# ---------------------------------------------------------------------------
# 1. Sharing owns who follows whom. Curation never learns about it.
# ---------------------------------------------------------------------------
Invoke-RestMethod -Method Post "$BaseUrl/api/follows" -ContentType $json `
    -Body '{"curatorId":"alice","followerId":"bob"}' | Out-Null
Invoke-RestMethod -Method Post "$BaseUrl/api/follows" -ContentType $json `
    -Body '{"curatorId":"alice","followerId":"carol"}' | Out-Null
Write-Host ""
Write-Host "== 1. bob and carol now follow alice (204 No Content, nothing to show)" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 2-3. The aggregate. A collection starts in Draft and cannot be published
# empty - see Collection.Publish.
# ---------------------------------------------------------------------------
$collection = Invoke-RestMethod -Method Post "$BaseUrl/api/collections" -ContentType $json `
    -Body '{"curatorId":"alice","name":"Distributed Systems Wisdom"}'
Show-Step "2. alice starts a collection (id minted by the domain, not the database)" $collection

$id = $collection.collectionId

Show-Step "3a. first quote added" (Invoke-RestMethod -Method Post "$BaseUrl/api/collections/$id/items" `
    -ContentType $json -Body '{"quoteId":1}')
Show-Step "3b. second quote added" (Invoke-RestMethod -Method Post "$BaseUrl/api/collections/$id/items" `
    -ContentType $json -Body '{"quoteId":2}')

# ---------------------------------------------------------------------------
# 4. Nothing has been announced yet, so nothing is in anyone's feed.
# ---------------------------------------------------------------------------
Show-Step "4. bob's feed before publishing" (Invoke-RestMethod "$BaseUrl/api/feed/bob")

# ---------------------------------------------------------------------------
# 5. The slice. This returns as soon as the state change and the outbox row
# are committed together, and says nothing about delivery on purpose.
# ---------------------------------------------------------------------------
Show-Step "5. publish - returns on commit, not on delivery" (Invoke-RestMethod -Method Post `
    "$BaseUrl/api/collections/$id/publish" -ContentType $json -Body '{"curatorId":"alice"}')

# ---------------------------------------------------------------------------
# 6-7. The two observations this whole walkthrough exists for.
# ---------------------------------------------------------------------------
Show-Step "6. the outbox, immediately: the announcement is durable and undelivered" `
    (Invoke-RestMethod "$BaseUrl/api/outbox")

Show-Step "7. bob's feed, immediately: still empty - eventually consistent by design" `
    (Invoke-RestMethod "$BaseUrl/api/feed/bob")

# ---------------------------------------------------------------------------
# 8-10. Let the relay run once.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== 8. waiting ${RelayWaitSeconds}s for one relay poll" -ForegroundColor Cyan
Start-Sleep -Seconds $RelayWaitSeconds

Show-Step "9. the same row, acknowledged - nothing was deleted, SentAt was stamped" `
    (Invoke-RestMethod "$BaseUrl/api/outbox")

Show-Step "10a. bob's feed" (Invoke-RestMethod "$BaseUrl/api/feed/bob")
Show-Step "10b. carol's feed - fan-out on write means one entry each" `
    (Invoke-RestMethod "$BaseUrl/api/feed/carol")

# ---------------------------------------------------------------------------
# 11. Publishing twice is refused by the aggregate, and - the half that
# matters - stages no second announcement. A duplicate fan-out would reach
# every follower.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== 11. publishing again" -ForegroundColor Cyan
try {
    Invoke-RestMethod -Method Post "$BaseUrl/api/collections/$id/publish" `
        -ContentType $json -Body '{"curatorId":"alice"}' | Out-Null
    Write-Host "UNEXPECTED: the second publish succeeded"
}
catch {
    # Reading the body off a failed Invoke-RestMethod is version-dependent and
    # the obvious way is wrong. On PowerShell 7 the body is in
    # $_.ErrorDetails.Message; on 5.1 that is null and the body is only
    # available by reading the response stream. The first version of this
    # script used ErrorDetails alone and printed an empty line where the
    # domain's own sentence was supposed to be - a demo that silently showed
    # nothing, which is the Day 29 lesson arriving in a new costume.
    $status = $_.Exception.Response.StatusCode.value__
    $body = $_.ErrorDetails.Message

    if (-not $body -and $_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        try { $body = $reader.ReadToEnd() } finally { $reader.Close() }
    }

    Write-Host "$status, with the domain's own sentence:"
    Write-Host $body
}

Show-Step "12. and still exactly one outbox row" (Invoke-RestMethod "$BaseUrl/api/outbox")

Write-Host ""
Write-Host "Done." -ForegroundColor Yellow
