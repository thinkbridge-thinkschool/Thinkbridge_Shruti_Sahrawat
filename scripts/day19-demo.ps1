<#
.SYNOPSIS
  Runs the Day 19 Service Bus demo end to end and captures the evidence.

.DESCRIPTION
  Starts the emulator, launches two worker instances in their own windows so the
  competing-consumer split is visible as it happens, publishes the demo message
  set, then writes the projections, the idempotency ledger and both dead-letter
  queues to Days/day-19/evidence.txt.

  Two windows rather than two background jobs on purpose: the interleaved log
  output IS half the evidence, and a background job swallows it.

.PARAMETER SkipEmulatorStart
  Use when the emulator is already running.

.PARAMETER KeepWorkers
  Leave the worker windows open when the run finishes.
#>
param(
    [switch]$SkipEmulatorStart,
    [switch]$KeepWorkers
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$workerProject = Join-Path $repoRoot "Quotes.Worker"
$evidenceDir  = Join-Path $repoRoot "Days\day-19"
$evidencePath = Join-Path $evidenceDir "evidence.txt"
$databasePath = Join-Path $repoRoot "quotes-messaging.db"

function Write-Step($text) {
    Write-Host ""
    Write-Host ">> $text" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- emulator ---
if (-not $SkipEmulatorStart) {
    Write-Step "Starting the Service Bus emulator"
    & (Join-Path $repoRoot "servicebus-emulator\start.ps1")
}
else {
    Write-Step "Skipping emulator start (assuming it is already up)"
}

# -------------------------------------------------------------- preflight ----
# Fail fast and legibly if the broker is not reachable. Without this the run
# gets as far as launching two worker windows and publishing before dying in an
# AMQP stack trace whose top frame names whichever method happened to connect
# first - which says nothing about the actual problem, that the namespace is
# gone or the machine is offline.
$preflightSettings = Get-Content (Join-Path $PSScriptRoot "..\Quotes.Worker\appsettings.json") -Raw | ConvertFrom-Json
$preflightNamespace = $preflightSettings.ServiceBus.FullyQualifiedNamespace
if (-not [string]::IsNullOrWhiteSpace($preflightNamespace)) {
    Write-Step "Checking the Service Bus namespace is reachable"
    try {
        [System.Net.Dns]::GetHostEntry($preflightNamespace) | Out-Null
        Write-Host "   $preflightNamespace resolves"
    }
    catch {
        throw ("Cannot resolve $preflightNamespace - the namespace has probably been deleted, " +
               "or this machine is offline. Recreate it (see Days/day-19/AZURE-NOTES.md) or point " +
               "Quotes.Worker/appsettings.json at the local emulator.")
    }
}

# ------------------------------------------------------- kill stale workers ---
# A previous run left with -KeepWorkers, or a worker window was never closed.
# Those processes hold both Quotes.Worker.exe and the SQLite file open, so the
# build fails with MSB3027 and the database reset fails with an IOException -
# neither of which names the actual cause, which is simply "an old worker is
# still running".
Write-Step "Stopping any worker instances left over from a previous run"
$stale = Get-Process -Name "Quotes.Worker" -ErrorAction SilentlyContinue
if ($stale) {
    $stale | Stop-Process -Force
    Write-Host "   stopped $($stale.Count) leftover worker process(es)"
    # Windows does not release the file handles synchronously with the process
    # exiting, so building immediately after can still hit the same lock.
    Start-Sleep -Seconds 3
}
else {
    Write-Host "   none running"
}

# ---------------------------------------------------------------- clean db ---
# A fresh ledger each run, otherwise every message is a duplicate of the last
# run and the demo proves nothing except that the ledger still works.
Write-Step "Resetting the local consumer database"
foreach ($suffix in @("", "-wal", "-shm")) {
    $file = "$databasePath$suffix"
    if (Test-Path $file) { Remove-Item $file -Force }
}
Write-Host "   removed $databasePath (and any WAL sidecars)"

# ----------------------------------------------------------------- build -----
Write-Step "Building"
dotnet build $workerProject -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# ------------------------------------------------------------ schema init ---
# Create the schema once, up front, single-threaded. EnsureCreated is
# check-then-create, not atomic - two worker instances starting at the same
# instant against a brand-new database file both race to create it, and the
# loser can throw and exit before ever reaching the consuming loop. Doing it
# here, before either worker exists, removes the race rather than papering
# over it; Program.cs also retries this itself as defence in depth, but
# proving competing consumers actually compete needs BOTH instances to
# survive startup, not just one of them.
Write-Step "Creating the consumer database schema"
Push-Location $workerProject
try {
    dotnet run --no-build -- report | Out-Null
}
finally { Pop-Location }
Write-Host "   schema ready"

# ------------------------------------------------------------- purge dlq ----
# Dead-letter queues are never drained by anything except deliberate action -
# that is what makes them a safety net rather than a log. So a second run
# would otherwise report the first run's dead letters next to its own, and
# the evidence would stop being readable.
Write-Step "Draining dead-letter queues from any previous run"
Push-Location $workerProject
try {
    dotnet run --no-build -- purge-dlq
}
finally { Pop-Location }

# --------------------------------------------------------------- workers -----
Write-Step "Launching two competing-consumer worker instances"
$workerWindows = @()
foreach ($name in @("worker-1", "worker-2")) {
    # Tee, not redirect: the window stays readable while the same output is
    # captured to a file. Without this the only record of a duplicate actually
    # being rejected is a line on a screen that closes when the run ends - and
    # a claim about idempotency with no artifact behind it is not evidence.
    $logPath = Join-Path $evidenceDir "$name.log"
    $command = "`$host.UI.RawUI.WindowTitle = '$name'; " +
               "`$env:WORKER_INSTANCE = '$name'; " +
               "Set-Location '$workerProject'; " +
               "dotnet run --no-build 2>&1 | Tee-Object -FilePath '$logPath'"
    $workerWindows += Start-Process powershell -PassThru -ArgumentList @("-NoExit", "-Command", $command)
    Write-Host "   started $name"
    # A short stagger, not to fix a correctness bug (the schema is already
    # created above) but so the two windows do not visually race to grab the
    # Azure AD token cache on first launch - purely cosmetic, keeps the two
    # startup logs from interleaving into an unreadable mess.
    Start-Sleep -Seconds 2
}

Write-Host "   waiting 25s for both to connect and start consuming..."
Start-Sleep -Seconds 25

# --------------------------------------------------------------- publish -----
Write-Step "Publishing the demo message set"
Push-Location $workerProject
try {
    dotnet run --no-build -- publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
}
finally { Pop-Location }

# The always-failing message needs three full delivery attempts before the
# broker dead-letters it, and each one waits out a lock.
Write-Step "Waiting 45s for retries to exhaust and the broker to dead-letter"
Start-Sleep -Seconds 45

# -------------------------------------------------------------- evidence -----
Write-Step "Capturing evidence"
Push-Location $workerProject
try {
    $report = dotnet run --no-build -- report | Out-String
    $deadLetters = dotnet run --no-build -- dlq | Out-String
}
finally { Pop-Location }

# Read the actual target out of configuration rather than asserting one. The
# same script runs against the local emulator and against a real namespace, and
# an evidence file that names the wrong broker is worse than one that names
# none - it is a claim nobody checked.
$appSettings = Get-Content (Join-Path $workerProject "appsettings.json") -Raw | ConvertFrom-Json
$namespace = $appSettings.ServiceBus.FullyQualifiedNamespace
$target = if ([string]::IsNullOrWhiteSpace($namespace)) {
    "local Service Bus emulator (docker), connection string auth"
} else {
    "Azure Service Bus Standard namespace $namespace, DefaultAzureCredential (no key)"
}

# Read MaxDeliveryCount from wherever it is actually configured rather than
# asserting it - the value is load-bearing for the dead-letter demonstration,
# so a stale literal here would misdescribe the very thing being proven.
$maxDeliveryCount = "unknown"
if ([string]::IsNullOrWhiteSpace($namespace)) {
    $emulatorConfig = Get-Content (Join-Path $repoRoot "servicebus-emulator\config.json") -Raw | ConvertFrom-Json
    $maxDeliveryCount = $emulatorConfig.UserConfig.Namespaces[0].Topics[0].Subscriptions[0].Properties.MaxDeliveryCount
}
else {
    $shortName = $namespace.Split(".")[0]
    $maxDeliveryCount = az servicebus topic subscription show `
        --name $appSettings.ServiceBus.SearchIndexerSubscription `
        --topic-name $appSettings.ServiceBus.TopicName `
        --namespace-name $shortName `
        --resource-group rg-thinkschool-dev2 `
        --query maxDeliveryCount -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($maxDeliveryCount)) { $maxDeliveryCount = "unknown (az query failed)" }
}

$header = @"
Day 19 - Azure Service Bus topics + DLQ
Captured $(Get-Date -Format "yyyy-MM-dd HH:mm:ss K")
Target: $target
Topic: $($appSettings.ServiceBus.TopicName)   Subscriptions: $($appSettings.ServiceBus.SearchIndexerSubscription) (SQL filter), $($appSettings.ServiceBus.AuditLogSubscription) (catch-all)
MaxDeliveryCount: $maxDeliveryCount   Worker instances: worker-1, worker-2

"@

# Pull the lines that prove the ledger actually rejected a redelivery. These
# come from the workers themselves, not from a summary this script wrote.
$duplicateLines = @()
foreach ($name in @("worker-1", "worker-2")) {
    $logPath = Join-Path $evidenceDir "$name.log"
    if (Test-Path $logPath) {
        $matches = Select-String -Path $logPath -Pattern "Duplicate ignored" -SimpleMatch
        foreach ($m in $matches) { $duplicateLines += "  [$name] $($m.Line.Trim())" }
    }
}

$duplicateSection = @"

==============================================================================
IDEMPOTENCY: REDELIVERIES REJECTED (from the worker consoles)
==============================================================================

"@
if ($duplicateLines.Count -gt 0) {
    $duplicateSection += ($duplicateLines -join "`n") + "`n"
}
else {
    $duplicateSection += "  (none logged - the replay was not observed being rejected this run)`n"
}

$evidence = $header + $report + $duplicateSection + $deadLetters
[System.IO.File]::WriteAllText($evidencePath, $evidence, [System.Text.UTF8Encoding]::new($false))

Write-Host $report
Write-Host $deadLetters
Write-Host "Evidence written to $evidencePath" -ForegroundColor Green

# --------------------------------------------------------------- cleanup -----
if (-not $KeepWorkers) {
    Write-Step "Stopping worker instances"
    foreach ($window in $workerWindows) {
        if (-not $window.HasExited) { Stop-Process -Id $window.Id -Force }
    }
}
else {
    Write-Host ""
    Write-Host "Worker windows left open (-KeepWorkers)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. Emulator is still running - stop it with servicebus-emulator\stop.ps1" -ForegroundColor Green
