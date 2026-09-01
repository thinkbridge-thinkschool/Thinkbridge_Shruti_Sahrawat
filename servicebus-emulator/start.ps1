# Starts the local Service Bus emulator.
#
# Generates .env with a random SA password on first run so no credential is
# ever typed into a file that git can see. Windows PowerShell 5.1 compatible:
# RandomNumberGenerator.Fill is .NET 6+ only, so this uses Create()+GetBytes().
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

if (-not (Test-Path ".env")) {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    # SQL Server rejects passwords without mixed case, a digit and a symbol,
    # so a raw base64 string is not always accepted - append a known-good tail.
    $pwd = [Convert]::ToBase64String($bytes).Replace("/", "_").Replace("+", "-") + "aA1!"
    $content = "MSSQL_SA_PASSWORD=$pwd`n"
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot ".env"), $content, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Generated .env with a random local-only SA password (gitignored)." -ForegroundColor Green
}

Write-Host "Starting emulator (first run pulls ~1.5 GB of images)..." -ForegroundColor Cyan
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }

Write-Host ""
Write-Host "Waiting for the emulator to report ready..." -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 3
    $logs = docker logs quotes-sb-emulator 2>&1 | Out-String
    if ($logs -match "Emulator Service is Successfully Up" -or $logs -match "Now listening on") {
        $ready = $true; break
    }
    if ($logs -match "Emulator failed to start" -or $logs -match "FATAL") {
        Write-Host $logs -ForegroundColor Red
        throw "Emulator reported a startup failure - see the log above."
    }
    Write-Host "  ...still starting ($([int](($i+1)*3))s)" -ForegroundColor DarkGray
}

if (-not $ready) {
    Write-Host "Emulator did not report ready within 3 minutes. Last 40 log lines:" -ForegroundColor Yellow
    docker logs --tail 40 quotes-sb-emulator
    throw "Emulator not ready."
}

Write-Host ""
Write-Host "Emulator ready on amqp://localhost:5672" -ForegroundColor Green
Write-Host "  topic:         quote-events" -ForegroundColor Green
Write-Host "  subscriptions: search-indexer (SQL filter), audit-log (catch-all)" -ForegroundColor Green
