<#
.SYNOPSIS
Day 27 - what does this deployment actually answer, and with which headers?

.DESCRIPTION
The ZAP baseline scan spiders from the root URL, and a JSON API with no HTML
gives it three URLs and nothing to follow - so "66 PASS" from a baseline scan
says very little about which endpoints are exposed. This asks the question
directly: for each endpoint that today's hardening is supposed to remove from
Production, is it reachable, and does the response carry the security headers
the middleware adds?

Run it against the deployed app and against a local instance started with
ASPNETCORE_ENVIRONMENT=Production. The difference between Development and
Production on the *same binary* is the entire point of gating diagnostics on
the environment rather than deleting the endpoints.

Expected on a HARDENED Production deployment: /health and the auth endpoints
answer; every diagnostic path 404s; every security header present.

Written for Windows PowerShell 5.1 as well as 7 - 5.1 has no
-SkipHttpErrorCheck and throws on any non-2xx, and its header collections are
a different type, so both are handled below rather than assumed.

.PARAMETER BaseUrl
Root of the deployment to probe.

.PARAMETER OutFile
Optional path to also write the tables to, for the write-up.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaseUrl,

    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
$BaseUrl = $BaseUrl.TrimEnd('/')

# 5.1 throws a WebException on 4xx/5xx and 7 throws HttpResponseException;
# both carry the response, and a 404 is an answer to this question, not an
# error. Anything with no response at all is a genuine failure to reach.
function Invoke-Probe {
    param([string]$Url)
    try {
        $r = Invoke-WebRequest -Uri $Url -Method GET -TimeoutSec 30 -UseBasicParsing
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Headers = $r.Headers }
    }
    catch {
        $resp = $null
        try { $resp = $_.Exception.Response } catch { }
        if ($resp) {
            $code = try { [int]$resp.StatusCode } catch { $resp.StatusCode }
            $hdrs = try { $resp.Headers } catch { $null }
            return [pscustomobject]@{ Code = $code; Headers = $hdrs }
        }
        return [pscustomobject]@{ Code = "ERR: $($_.Exception.Message -replace '\s+', ' ')"; Headers = $null }
    }
}

# WebHeaderCollection (5.1 error path) indexes by name; the dictionary forms
# need a case-insensitive walk, and 7 stores values as arrays.
function Get-HeaderValue {
    param($Headers, [string]$Name)
    if ($null -eq $Headers) { return $null }
    try {
        if ($Headers -is [System.Net.WebHeaderCollection]) {
            return $Headers[$Name]
        }
        foreach ($k in $Headers.Keys) {
            if ($k -ieq $Name) { return (($Headers[$k]) -join '; ') }
        }
    }
    catch { return $null }
    return $null
}

# GET routes wherever one exists, so the signal is 200-vs-404 rather than
# 405. A 405 does prove a route is present - it is the POST-only endpoints
# answering a GET - but it reads like a failure at a glance, and half this
# table's job is being obvious. Route names verified against
# ResilienceEndpoints.cs / CacheDiagnosticsEndpoints.cs rather than guessed;
# the first version of this list invented /api/resilience/state, which does
# not exist, and a 404 on a route that was never there looks identical to a
# route that was correctly gated off.
$paths = @(
    [pscustomobject]@{ Path = '/health';                            Gated = $false }
    [pscustomobject]@{ Path = '/api/auth/login';                    Gated = $false }
    [pscustomobject]@{ Path = '/api/quotes';                        Gated = $false }
    [pscustomobject]@{ Path = '/openapi/v1.json';                   Gated = $true  }
    [pscustomobject]@{ Path = '/api/demo/resilience';               Gated = $true  }
    [pscustomobject]@{ Path = '/api/profiling/author-stats-fast';   Gated = $true  }
    [pscustomobject]@{ Path = '/api/profiling/author-stats-slow';   Gated = $true  }
    [pscustomobject]@{ Path = '/api/cache/stats';                   Gated = $true  }
    [pscustomobject]@{ Path = '/api/upstream/status';               Gated = $true  }
    [pscustomobject]@{ Path = '/api/upstream/state';                Gated = $true  }
    [pscustomobject]@{ Path = '/api/resilience/stats';              Gated = $true  }
)

$securityHeaders = @(
    'X-Content-Type-Options'
    'X-Frame-Options'
    'Content-Security-Policy'
    'Referrer-Policy'
    'Cross-Origin-Resource-Policy'
    'Strict-Transport-Security'
)

Write-Host "Probing $BaseUrl"
Write-Host ""

$rows = foreach ($p in $paths) {
    $res = Invoke-Probe -Url "$BaseUrl$($p.Path)"
    $code = $res.Code

    # A gated path is correct when it is gone. An ungated one is correct when
    # it answers at all - 401 on /api/quotes is authorisation working, not a
    # missing endpoint. 405 on a GET to a POST-only route likewise means the
    # route is there.
    $verdict =
        if ($code -isnot [int]) { "UNREACHABLE" }
        elseif ($p.Gated) { if ($code -eq 404) { 'OK (gated off)' } else { "EXPOSED ($code)" } }
        else { if ($code -eq 404) { "MISSING (404)" } else { "OK ($code)" } }

    [pscustomobject]@{
        Path    = $p.Path
        Status  = $code
        Gated   = $p.Gated
        Verdict = $verdict
    }
}

$rows | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "Security headers on /health:"
$probe = Invoke-Probe -Url "$BaseUrl/health"
$headerRows = foreach ($name in $securityHeaders) {
    $v = Get-HeaderValue -Headers $probe.Headers -Name $name
    [pscustomobject]@{
        Header  = $name
        Present = if ([string]::IsNullOrWhiteSpace($v)) { 'NO' } else { 'yes' }
        Value   = $v
    }
}
$serverHeader = Get-HeaderValue -Headers $probe.Headers -Name 'Server'
$headerRows += [pscustomobject]@{
    Header  = 'Server'
    Present = if ([string]::IsNullOrWhiteSpace($serverHeader)) { 'absent (good)' } else { 'present' }
    Value   = $serverHeader
}
$headerRows | Format-Table -AutoSize | Out-String | Write-Host

if ($OutFile) {
    $out = @()
    $out += "Probed: $BaseUrl"
    $out += "At:     $([DateTimeOffset]::UtcNow.ToString('u'))"
    $out += ""
    $out += ($rows | Format-Table -AutoSize | Out-String)
    $out += "Security headers on /health:"
    $out += ($headerRows | Format-Table -AutoSize | Out-String)
    $out -join "`n" | Set-Content -Path $OutFile -Encoding utf8
    Write-Host "written to $OutFile"
}
