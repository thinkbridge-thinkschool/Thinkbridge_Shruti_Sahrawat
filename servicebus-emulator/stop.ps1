# Tears the emulator down. -RemoveVolumes also drops the SQL state, which is
# how you reset the topic to empty between demo runs.
param([switch]$RemoveVolumes)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
if ($RemoveVolumes) { docker compose down -v } else { docker compose down }
Write-Host "Emulator stopped." -ForegroundColor Green
