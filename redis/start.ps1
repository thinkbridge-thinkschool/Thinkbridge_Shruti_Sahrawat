# Starts Redis for Day 21's L2 cache tier.
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host "Starting Redis..." -ForegroundColor Cyan
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }

Write-Host "Waiting for Redis to answer PING..." -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 2
    $pong = docker exec quotes-redis redis-cli ping 2>&1 | Out-String
    if ($pong -match "PONG") { $ready = $true; break }
}

if (-not $ready) {
    docker logs --tail 40 quotes-redis
    throw "Redis did not answer PING within 40 seconds."
}

Write-Host ""
Write-Host "Redis ready on localhost:6379" -ForegroundColor Green
Write-Host 'Point the API at it:  $env:Cache__RedisConnectionString = "localhost:6379,abortConnect=false"' -ForegroundColor Green
