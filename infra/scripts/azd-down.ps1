<#
Day 24 - the teardown counterpart to azd-provision.ps1.

    ./scripts/azd-down.ps1 -Environment dev
    ./scripts/azd-down.ps1 -Environment prod

Exists because `azd down` compiles the Bicep template before it deletes
anything, so every parameter without a default has to be present merely to
perform a deletion. For prod those are JWT_SIGNING_KEY and
API_CONTAINER_IMAGE, and both are deliberately default-less: that is the
guardrail stopping anyone provisioning prod against a placeholder image or a
placeholder signing key (see main.prod.bicepparam, and Days/day-24).

The guardrail is right about deploying and wrong about deleting. Run
`azd down` without them and it dies on BCP427 having deleted nothing, while
the resources keep billing - a failure whose only symptom is a bill, which is
the worst possible direction for a teardown to fail in.

So this script supplies placeholders for the compile, and only for the
compile. A deletion never reads these values; they exist to satisfy
`bicep build-params` and nothing else. It supplies them ONLY where the
variable is absent, so a real value in the session is never overwritten, and
it never touches the provision path - `azd-provision.ps1` still fails loudly
if a real key or image is missing, exactly as before.

Always purges the Key Vault. Without --purge the vault is soft-deleted, its
name stays reserved, and the next provision of the same environment collides
with a vault it cannot see (Day 25 hit precisely that).

Verifies afterwards against the resource provider rather than against azd's
own report. Day 24, Finding 4 is the run where `azd down` announced success
in five seconds having deleted nothing at all.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment
)

$ErrorActionPreference = 'Stop'

# See azd-provision.ps1 for why this is switched off: PowerShell 7.4 turns a
# non-zero native exit into a terminating error before the next line runs,
# which makes the explicit $LASTEXITCODE checks below dead code.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$scriptRoot = $PSScriptRoot
$infraDir = Split-Path $scriptRoot -Parent
Push-Location $infraDir
try {
    # main.bicepparam is generated, and `azd down` compiles whichever copy is
    # currently on disk. Selecting first means a teardown of prod cannot
    # compile dev's parameters by accident.
    & (Join-Path $scriptRoot 'select-bicepparam.ps1') -EnvironmentName $Environment
    if ($LASTEXITCODE -ne 0) { throw "select-bicepparam.ps1 failed for '$Environment'." }

    azd env select $Environment
    if ($LASTEXITCODE -ne 0) {
        throw "azd env select '$Environment' failed. Nothing was deleted."
    }

    # Placeholders for the compile only, and only where nothing real is set.
    $placeholders = @{
        JWT_SIGNING_KEY     = 'teardown-placeholder-nothing-signs-with-this-0000000000'
        API_CONTAINER_IMAGE = 'mcr.microsoft.com/k8se/quickstart:latest'
    }
    foreach ($name in $placeholders.Keys) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            Set-Item -Path "env:$name" -Value $placeholders[$name]
            Write-Host "supplied a compile-time placeholder for $name (a deletion never reads it)"
        }
        else {
            Write-Host "$name already set in this session - left alone"
        }
    }

    $stack = "azd-stack-$Environment"

    # Record what the stack manages BEFORE deleting, so the check afterwards is
    # against something real rather than against the absence of an error.
    $groupsBefore = @()
    $ids = az stack sub show --name $stack --query "resources[].id" -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and $ids) {
        $groupsBefore = @($ids |
            ForEach-Object { if ($_ -match '/resourceGroups/([^/]+)/') { $Matches[1] } } |
            Sort-Object -Unique)
        Write-Host "`nstack '$stack' currently manages resources in: $($groupsBefore -join ', ')"
    }
    else {
        Write-Host "`nno stack named '$stack' found - there may be nothing to delete"
    }

    Write-Host "`n=== azd down ($Environment) - this deletes real resources ===`n"
    azd down --force --purge
    $downExit = $LASTEXITCODE

    Write-Host "`n=== verifying against Azure, not against azd ===`n"

    az stack sub show --name $stack -o none 2>$null
    $stackGone = ($LASTEXITCODE -ne 0)
    Write-Host ("deployment stack '{0}': {1}" -f $stack, $(if ($stackGone) { 'gone' } else { 'STILL PRESENT' }))

    $survivors = @()
    foreach ($g in $groupsBefore) {
        $exists = az group exists -n $g
        Write-Host ("resource group '{0}': {1}" -f $g, $(if ($exists -eq 'true') { 'STILL PRESENT' } else { 'gone' }))
        if ($exists -eq 'true') { $survivors += $g }
    }

    if ($stackGone -and $survivors.Count -eq 0) {
        Write-Host "`nteardown confirmed: nothing from '$stack' is left billing."
        exit 0
    }

    Write-Warning "teardown did NOT fully complete (azd exit code $downExit)."
    if ($survivors.Count -gt 0) {
        Write-Warning "these resource groups still exist and are still billing: $($survivors -join ', ')"
    }
    exit 1
}
finally {
    Pop-Location
}
