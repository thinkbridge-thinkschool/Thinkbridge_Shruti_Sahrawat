<#
.SYNOPSIS
  Seeds the live QuotesApi with synthetic placeholder quotes, for demoing
  pagination and the card-grid list with something other than an empty page.

.DESCRIPTION
  Every quote/author below is invented filler text for this exercise, not a
  real quote attributed to a real person -- generic placeholder content only.
  POSTs to /api/quotes, whose contract (QuotesApi/Models/QuoteDtos.cs) is
  { author: string, text: string }, both required, Author <= 200 chars,
  Text <= 1000 chars.

.PARAMETER Count
  How many quotes to create. Default 40 -- enough for four pages at the
  app's default page size of 10.

.PARAMETER ApiBaseUrl
  Base URL of the live QuotesApi. Defaults to the current deployment.

.EXAMPLE
  ./scripts/seed-quotes.ps1
.EXAMPLE
  ./scripts/seed-quotes.ps1 -Count 100
#>
param(
  [int]$Count = 40,
  [string]$ApiBaseUrl = "https://quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io"
)

$texts = @(
  "Small steps, taken daily, outrun big plans taken never.",
  "The quietest ideas are often the ones worth writing down.",
  "A good question is worth more than a fast answer.",
  "Progress hides in the work nobody claps for.",
  "Clarity is a habit, not a talent.",
  "What you measure gets better; what you ignore gets worse.",
  "The first draft's only job is to exist.",
  "Curiosity is a renewable resource.",
  "Consistency beats intensity, most days.",
  "Every expert was once a beginner who kept going.",
  "The best time to start was earlier. The next best time is now.",
  "Simplicity is the art of leaving out the unnecessary.",
  "A plan is just a guess with a deadline.",
  "Discipline is choosing between what you want now and what you want most.",
  "Done is better than perfect, and shipped is better than done.",
  "Patience is a skill disguised as a virtue.",
  "The obstacle in the way often becomes the way forward.",
  "Learning never exhausts the mind, it only sharpens it.",
  "Growth lives just outside the comfortable zone.",
  "A calm mind sees what a rushed one misses."
)

$authors = @(
  "A. Rivera", "J. Okafor", "M. Chen", "S. Alaoui", "K. Novak",
  "R. Fontaine", "T. Osei", "L. Bergstrom", "P. Nakamura", "D. Kowalski",
  "N. Abioye", "E. Larsson", "H. Duarte", "V. Marchetti", "C. Whitfield"
)

Write-Host "Seeding $Count quote(s) into $ApiBaseUrl ..."

$created = 0
$failed = 0

for ($i = 1; $i -le $Count; $i++) {
  $text = $texts[$i % $texts.Length]
  $author = $authors[$i % $authors.Length]

  $body = @{ author = $author; text = $text } | ConvertTo-Json

  try {
    $response = Invoke-RestMethod -Uri "$ApiBaseUrl/api/quotes" -Method Post -ContentType "application/json" -Body $body
    $created++
    if ($created % 10 -eq 0) {
      Write-Host "  $created / $Count created..."
    }
  } catch {
    $failed++
    Write-Host "  Failed on #$i : $($_.Exception.Message)"
  }
}

Write-Host ""
Write-Host "Done. Created $created, failed $failed."
