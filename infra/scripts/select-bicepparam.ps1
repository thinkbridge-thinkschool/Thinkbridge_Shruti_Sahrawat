<#
Day 24 - selects the .bicepparam matching an azd environment.

azd's Bicep provider reads exactly one parameters file per module name: for
module `main` that is `main.bicepparam`. There is no documented way to point
azd at `main.dev.bicepparam` directly, and a dotted module name like
`main.dev` is not a name Bicep or azd resolve to a `main.dev.bicep` +
`main.dev.bicepparam` pair. So the file has to be produced under the name azd
expects, from whichever of the two real files applies.

TIMING IS THE WHOLE POINT OF THIS SCRIPT NOT BEING A HOOK ON ITS OWN.
This started life as azd's `preprovision` hook and that does not work: azd
resolves infrastructure parameters *before* it runs preprovision, so a hook
that writes main.bicepparam writes it one step too late, and azd - finding no
parameters file - falls back to prompting interactively for every parameter it
cannot resolve from a convention-named environment variable. That includes
`sqlDatabaseSku` and `serviceBusSubscriptions`, which are an object and an
array of objects and cannot be answered at a text prompt at all. Verified on
azd 1.31.1, `azd provision --preview`, dev environment.

So this runs *before* azd is invoked - see scripts/azd-provision.ps1, which is
the entry point. It is still also wired as the preprovision hook in
azure.yaml, where it is a cheap re-assertion that the file on disk matches the
environment being deployed, not the thing that creates it in time.

infra/main.dev.bicepparam and infra/main.prod.bicepparam remain the real,
committed sources. infra/main.bicepparam is generated, gitignored, and never
the file to hand-edit.
#>

[CmdletBinding()]
param(
    # Explicit when called directly (or by azd-provision.ps1). Falls back to
    # AZURE_ENV_NAME, which azd sets for every hook, when run as a hook.
    [string]$EnvironmentName = $env:AZURE_ENV_NAME
)

$ErrorActionPreference = 'Stop'

if (-not $EnvironmentName) {
    throw "No environment name. Pass -EnvironmentName dev|prod, or run this as an azd hook where AZURE_ENV_NAME is set."
}

$infraDir = Split-Path $PSScriptRoot -Parent
$source = Join-Path $infraDir "main.$EnvironmentName.bicepparam"
$dest = Join-Path $infraDir "main.bicepparam"

if (-not (Test-Path $source)) {
    throw "No parameter file for environment '$EnvironmentName' - expected $source. This project only has parameter files for: dev, prod."
}

Copy-Item -Path $source -Destination $dest -Force
Write-Host "environment '$EnvironmentName' -> infra/main.bicepparam now mirrors $(Split-Path $source -Leaf)"
