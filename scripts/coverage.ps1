<#
.SYNOPSIS
    Run every test project, merge the coverage reports, and check the gate.

.DESCRIPTION
    The local equivalent of the "Coverage gate" job in CI, so the number you see
    here is the number the pipeline will see. It runs each test project with the
    shared coverlet.runsettings, then takes the union of the Cobertura reports:
    a line counts as covered if any test project executed it.

    Depends on nothing but the .NET SDK and PowerShell.

.PARAMETER Threshold
    Minimum merged line coverage percentage. Defaults to 80, matching CI.

.PARAMETER SkipIntegration
    Skip Quotes.Tests.Integration, which needs Docker Desktop running for the
    SQL Server 2022 Testcontainer. The reported number will be lower than CI's.

.EXAMPLE
    ./scripts/coverage.ps1
    ./scripts/coverage.ps1 -SkipIntegration
    ./scripts/coverage.ps1 -Threshold 70
#>
[CmdletBinding()]
param(
    [int]$Threshold = 80,
    [switch]$SkipIntegration
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $projects = @('OrderRefactor.Tests', 'Quotes.Tests.Unit')
    if ($SkipIntegration) {
        Write-Host 'Skipping Quotes.Tests.Integration (Docker not required).' -ForegroundColor Yellow
    }
    else {
        $projects += 'Quotes.Tests.Integration'
    }

    foreach ($project in $projects) {
        $results = Join-Path $repoRoot "$project/TestResults"
        if (Test-Path $results) { Remove-Item -Recurse -Force $results }

        Write-Host ''
        Write-Host "==> $project" -ForegroundColor Cyan

        dotnet test "$project/$project.csproj" `
            --collect:"XPlat Code Coverage" `
            --settings coverlet.runsettings `
            --results-directory $results

        if ($LASTEXITCODE -ne 0) {
            throw "$project failed. Fix the tests before looking at coverage."
        }
    }

    $reports = Get-ChildItem -Path $repoRoot -Recurse -Filter 'coverage.cobertura.xml' `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*TestResults*' }

    if (-not $reports) { throw 'No coverage.cobertura.xml was produced.' }

    # Union the reports. Key is "file|lineNumber", value is the highest hit count
    # any project recorded for that line.
    $merged = @{}
    foreach ($report in $reports) {
        [xml]$doc = Get-Content -LiteralPath $report.FullName

        # Cobertura stores a <sources> root and writes each class's filename
        # relative to it. Coverlet does not pick the same root for every run, so
        # one report can say "Program.cs" where another says
        # "QuotesApi/Program.cs". Keying on the raw filename fails to merge those
        # two, and the failure flatters nothing: the file lands in the totals
        # twice, once with real coverage and once at zero, and the zero copy
        # drags the percentage down. That is exactly how this reported 29.96%
        # while the integration suite was demonstrably covering the same files.
        $sourceRoot = ''
        $sourceNode = $doc.SelectSingleNode('//sources/source')
        if ($sourceNode -and $sourceNode.InnerText) {
            $sourceRoot = ($sourceNode.InnerText.Trim() -replace '\\', '/').TrimEnd('/')
        }

        foreach ($class in $doc.SelectNodes('//class')) {
            $file = $class.filename
            if (-not $file) { $file = $class.name }
            $file = ($file -replace '\\', '/').TrimEnd('/')

            $isAbsolute = $file.StartsWith('/') -or ($file.Length -gt 1 -and $file[1] -eq ':')
            if (-not $isAbsolute -and $sourceRoot) { $file = "$sourceRoot/$file" }
            $file = $file.ToLowerInvariant()

            foreach ($line in $class.SelectNodes('lines/line')) {
                $key = "$file|$($line.number)"
                $hits = [int]$line.hits
                if (-not $merged.ContainsKey($key) -or $merged[$key] -lt $hits) {
                    $merged[$key] = $hits
                }
            }
        }
    }

    $total = $merged.Count
    $covered = ($merged.Values | Where-Object { $_ -gt 0 }).Count
    $rate = if ($total) { $covered / $total * 100 } else { 0 }

    # Per-file gaps, worst first.
    $rootPrefix = (($repoRoot -replace '\\', '/').TrimEnd('/') + '/').ToLowerInvariant()
    $byFile = @{}
    foreach ($entry in $merged.GetEnumerator()) {
        $file = $entry.Key.Substring(0, $entry.Key.LastIndexOf('|'))
        if (-not $byFile.ContainsKey($file)) {
            $byFile[$file] = [pscustomobject]@{ Covered = 0; Total = 0 }
        }
        $byFile[$file].Total++
        if ($entry.Value -gt 0) { $byFile[$file].Covered++ }
    }

    Write-Host ''
    Write-Host "Reports merged:  $($reports.Count)"
    Write-Host "Files:           $($byFile.Count)"
    Write-Host "Lines covered:   $covered / $total"
    Write-Host ("Line coverage:   {0:N2}%   (threshold {1}%)" -f $rate, $Threshold)
    Write-Host ''

    $gaps = $byFile.GetEnumerator() |
        ForEach-Object {
            $label = $_.Key
            if ($label.StartsWith($rootPrefix)) { $label = $label.Substring($rootPrefix.Length) }
            [pscustomobject]@{
                Missing = $_.Value.Total - $_.Value.Covered
                Covered = $_.Value.Covered
                Total   = $_.Value.Total
                File    = $label
            }
        } |
        Where-Object { $_.Missing -gt 0 } |
        Sort-Object Missing -Descending

    if ($gaps) {
        Write-Host 'Still uncovered, worst first:'
        $gaps | Select-Object -First 25 | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    }

    if ($rate -lt $Threshold) {
        Write-Host ("FAIL: merged line coverage {0:N2}% is below {1}%." -f $rate, $Threshold) -ForegroundColor Red
        exit 1
    }

    Write-Host ("PASS: merged line coverage {0:N2}% meets the {1}% gate." -f $rate, $Threshold) -ForegroundColor Green
}
finally {
    Pop-Location
}
