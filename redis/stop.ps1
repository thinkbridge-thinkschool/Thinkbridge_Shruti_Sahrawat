# Stops Redis. No volumes, so this discards the cache - which is the point.
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
docker compose down
