<#
    Day 22 - drives a running QuotesApi through the whole resilience pipeline
    and prints a timestamped timeline.

    Four phases, in this order for a reason:

      1. Baseline        - the dependency is healthy, calls succeed.
      2. Idempotency     - a POST with no key is not retried; the same POST
                           with a key is. Measured at the dependency.
      3. Bulkhead        - more concurrent calls than there are permits.
      4. Circuit breaker - sustained failure, the circuit opens, the
                           dependency stops being called, then it recovers.

    The breaker runs last because it is the only phase whose state Polly keeps
    between phases: the sampling window is 30 seconds, so failures produced
    earlier would still be counted. Running it last means the earlier phases
    cannot be distorted by it, and it opens a little sooner than it would from
    cold - which the script handles by polling the real state rather than
    assuming a call count.

    Usage - the API must already be running in another terminal:
        dotnet run --project QuotesApi
        pwsh perf/breaker-timeline.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5067",
    [int]$MaxFailingCalls = 8,
    [int]$ConcurrentCalls = 12
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http | Out-Null

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(120)

function Write-Step {
    param([string]$Text, [string]$Color = "Gray")
    Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $Text) -ForegroundColor $Color
}

function Write-Phase {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 78) -ForegroundColor DarkCyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ("=" * 78) -ForegroundColor DarkCyan
}

function Read-Body {
    param($Response)
    $text = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Invoke-Call {
    param([string]$Method = "GET", [string]$Path)

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method), "$BaseUrl$Path")

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $stopwatch.Stop()

    $body = Read-Body $response
    $outcome = ""
    if ($null -ne $body -and $null -ne $body.outcome) { $outcome = [string]$body.outcome }

    [pscustomobject]@{
        Status  = [int]$response.StatusCode
        Outcome = $outcome
        Ms      = $stopwatch.ElapsedMilliseconds
        Body    = $body
    }
}

function Get-Stats {
    $response = $http.GetAsync("$BaseUrl/api/resilience/stats").GetAwaiter().GetResult()
    return Read-Body $response
}

function Set-UpstreamMode {
    param([string]$Mode, [double]$SlowDelaySeconds = -1)

    $path = "/api/upstream/mode/$Mode"
    if ($SlowDelaySeconds -ge 0) { $path += "?slowDelaySeconds=$SlowDelaySeconds" }

    $response = $http.PostAsync("$BaseUrl$path", $null).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "Could not set upstream mode to '$Mode' (HTTP $([int]$response.StatusCode))."
    }
}

function Reset-Counters {
    $http.PostAsync("$BaseUrl/api/resilience/reset", $null).GetAwaiter().GetResult() | Out-Null
}

# --------------------------------------------------------------------------
# Preflight
# --------------------------------------------------------------------------

Write-Phase "Day 22 - resilience pipeline timeline"

try {
    $health = $http.GetAsync("$BaseUrl/health").GetAwaiter().GetResult()
    if (-not $health.IsSuccessStatusCode) { throw "unhealthy" }
}
catch {
    Write-Host ""
    Write-Host "The API is not answering at $BaseUrl." -ForegroundColor Red
    Write-Host "Start it in another terminal first:  dotnet run --project QuotesApi" -ForegroundColor Yellow
    exit 1
}

$config = (Get-Stats).configuration
Write-Step "API is up. Pipeline configuration:" "White"
Write-Step ("  bulkhead      {0} concurrent, queue {1}" -f $config.maxConcurrentCalls, $config.queueLimit)
Write-Step ("  timeouts      {0} per attempt, {1} total" -f $config.attemptTimeout, $config.totalRequestTimeout)
Write-Step ("  retry         up to {0}, idempotent requests only" -f $config.maxRetryAttempts)
Write-Step ("  breaker       {0} failure ratio over {1}, min throughput {2}, break {3}" -f `
    $config.failureRatio, $config.samplingDuration, $config.minimumThroughput, $config.breakDuration)

# --------------------------------------------------------------------------
# 1. Baseline
# --------------------------------------------------------------------------

Write-Phase "1. Baseline - the dependency is healthy"

Set-UpstreamMode -Mode "healthy"
Reset-Counters

for ($i = 1; $i -le 3; $i++) {
    $call = Invoke-Call -Path "/api/resilience/call"
    Write-Step ("call {0}  ->  HTTP {1}  {2}  ({3}ms)" -f $i, $call.Status, $call.Outcome, $call.Ms) "Green"
}

$stats = Get-Stats
Write-Step ("circuit={0}  upstream received {1} requests" -f $stats.circuitState, $stats.upstreamRequestsReceived) "White"

# --------------------------------------------------------------------------
# 2. Idempotency gate
# --------------------------------------------------------------------------

Write-Phase "2. Retry is idempotent-only"

Set-UpstreamMode -Mode "failing"
Reset-Counters

$call = Invoke-Call -Method "POST" -Path "/api/resilience/call"
$stats = Get-Stats
Write-Step ("POST with no key      -> HTTP {0}  {1}  ({2}ms)" -f $call.Status, $call.Outcome, $call.Ms) "Yellow"
Write-Step ("  upstream saw {0} request, retries suppressed: {1}" -f `
    $stats.upstreamRequestsReceived, $stats.retriesSuppressedAsNonIdempotent) "White"

Reset-Counters
$key = "day22-" + [guid]::NewGuid().ToString("N")
$call = Invoke-Call -Method "POST" -Path "/api/resilience/call?idempotencyKey=$key"
$stats = Get-Stats
Write-Step ("POST with a key       -> HTTP {0}  {1}  ({2}ms)" -f $call.Status, $call.Outcome, $call.Ms) "Yellow"
Write-Step ("  upstream saw {0} requests, retries taken: {1}" -f `
    $stats.upstreamRequestsReceived, $stats.retries) "White"
Write-Step "  same endpoint, same failure - the only difference is what the request claims about itself" "DarkGray"

# --------------------------------------------------------------------------
# 3. Bulkhead
# --------------------------------------------------------------------------

Write-Phase "3. Bulkhead - $ConcurrentCalls concurrent calls, $($config.maxConcurrentCalls) permits"

# Slow but successful: long enough to hold a permit, short enough not to trip
# the attempt timeout. A bulkhead is only observable while calls are occupying
# permits without failing.
Set-UpstreamMode -Mode "slow" -SlowDelaySeconds 1.5
Reset-Counters

$tasks = New-Object 'System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]'
$started = Get-Date

for ($i = 0; $i -lt $ConcurrentCalls; $i++) {
    $tasks.Add($http.GetAsync("$BaseUrl/api/resilience/call"))
}

[System.Threading.Tasks.Task]::WaitAll($tasks.ToArray())
$elapsed = [int]((Get-Date) - $started).TotalMilliseconds

$byStatus = @{}
foreach ($task in $tasks) {
    $status = [int]$task.Result.StatusCode
    if (-not $byStatus.ContainsKey($status)) { $byStatus[$status] = 0 }
    $byStatus[$status]++
}

foreach ($status in ($byStatus.Keys | Sort-Object)) {
    $label = switch ($status) {
        200 { "accepted and served" }
        429 { "rejected by the bulkhead" }
        default { "other" }
    }
    Write-Step ("{0} x HTTP {1}  ({2})" -f $byStatus[$status], $status, $label) "Yellow"
}

$stats = Get-Stats
Write-Step ("all $ConcurrentCalls returned in {0}ms; upstream received {1} of them" -f `
    $elapsed, $stats.upstreamRequestsReceived) "White"
Write-Step "  the rejected calls never reached the dependency - that is the difference from a queue" "DarkGray"

# --------------------------------------------------------------------------
# 4. Circuit breaker
# --------------------------------------------------------------------------

Write-Phase "4. Circuit breaker - sustained failure, then recovery"

Set-UpstreamMode -Mode "failing"
Reset-Counters

$openedAfter = 0
$upstreamWhenOpened = 0

for ($i = 1; $i -le $MaxFailingCalls; $i++) {
    $call = Invoke-Call -Path "/api/resilience/call"
    $stats = Get-Stats

    $colour = if ($call.Outcome -eq "ShortCircuited") { "Magenta" } else { "Red" }
    Write-Step ("call {0}  ->  HTTP {1}  {2}  ({3}ms)   circuit={4}  upstream={5}" -f `
        $i, $call.Status, $call.Outcome, $call.Ms, $stats.circuitState, $stats.upstreamRequestsReceived) $colour

    if ($stats.circuitState -eq "Open" -and $openedAfter -eq 0) {
        $openedAfter = $i
        $upstreamWhenOpened = $stats.upstreamRequestsReceived
        Write-Step ("  >> circuit OPEN after {0} call(s); the dependency had seen {1} requests" -f `
            $openedAfter, $upstreamWhenOpened) "Magenta"
    }
}

$stats = Get-Stats
if ($openedAfter -gt 0) {
    $spared = $stats.shortCircuits
    Write-Step ("while open: {0} call(s) refused locally, upstream still at {1} requests" -f `
        $spared, $stats.upstreamRequestsReceived) "Magenta"

    if ($stats.upstreamRequestsReceived -eq $upstreamWhenOpened) {
        Write-Step "  the dependency was not called once after the circuit opened" "Green"
    }
    else {
        Write-Step "  NOTE: the dependency was still called after the circuit opened" "Red"
    }
}
else {
    Write-Step "circuit never opened - raise -MaxFailingCalls or lower MinimumThroughput" "Red"
}

$break = [TimeSpan]::Parse($config.breakDuration)
Write-Step ("waiting out the {0}s break, then making the dependency healthy again" -f $break.TotalSeconds) "White"
Start-Sleep -Milliseconds ([int]$break.TotalMilliseconds + 500)

Set-UpstreamMode -Mode "healthy"

for ($i = 1; $i -le 3; $i++) {
    $call = Invoke-Call -Path "/api/resilience/call"
    $stats = Get-Stats
    Write-Step ("probe {0}  ->  HTTP {1}  {2}  ({3}ms)   circuit={4}  upstream={5}" -f `
        $i, $call.Status, $call.Outcome, $call.Ms, $stats.circuitState, $stats.upstreamRequestsReceived) "Green"
}

# --------------------------------------------------------------------------
# Summary
# --------------------------------------------------------------------------

Write-Phase "Final counters"

$stats = Get-Stats
[pscustomobject]@{
    "circuit state"              = $stats.circuitState
    "calls"                      = $stats.calls
    "successes"                  = $stats.successes
    "upstream failures"          = $stats.upstreamFailures
    "short circuits"             = $stats.shortCircuits
    "bulkhead rejections"        = $stats.bulkheadRejections
    "retries taken"              = $stats.retries
    "retries suppressed"         = $stats.retriesSuppressedAsNonIdempotent
    "attempt timeouts"           = $stats.attemptTimeouts
    "total-budget timeouts"      = $stats.totalTimeouts
    "breaker opened / closed"    = "$($stats.breakerOpened) / $($stats.breakerClosed)"
} | Format-List

Write-Host "Serilog wrote the state changes to the API's own console - the lines tagged" -ForegroundColor DarkGray
Write-Host "OutboundResiliencePipeline are the breaker opening, half-opening and closing." -ForegroundColor DarkGray
