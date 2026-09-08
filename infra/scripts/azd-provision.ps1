<#
Day 24 - the entry point for provisioning this stack through azd.

Wraps `azd provision` with the one step azd cannot do for itself: putting the
right main.bicepparam on disk *before* azd reads parameters. See
scripts/select-bicepparam.ps1 for why that cannot be a preprovision hook.

    ./scripts/azd-provision.ps1 -Environment dev -Preview   # plan, changes nothing
    ./scripts/azd-provision.ps1 -Environment dev            # deploy for real
    ./scripts/azd-provision.ps1 -Environment prod -Preview  # plan prod

Run from infra/. Everything it calls is ordinary azd - there is nothing here
that a person could not type by hand, and the -WhatIf-style dry run is azd's
own `--preview`, not a reimplementation of one.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    # azd's own --preview: a real plan against a real subscription that
    # creates nothing. The prod path stops here on purpose (Days/day-24).
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'

# 1. Parameters file first - before azd is invoked at all.
& (Join-Path $PSScriptRoot 'select-bicepparam.ps1') -EnvironmentName $Environment

# 2. Make sure azd is pointed at the matching environment. `azd env select`
#    fails loudly if the environment does not exist yet, which is the correct
#    behaviour: creating it silently here would hide a typo'd -Environment
#    behind a brand-new, empty environment.
azd env select $Environment
if ($LASTEXITCODE -ne 0) {
    throw "azd env select '$Environment' failed. Create it first: azd env new $Environment --location southindia"
}

# 3. Provision. Deployment Stacks come from azure.yaml's infra.deploymentStacks
#    block plus `azd config set alpha.deployment.stacks on` - neither is passed
#    here, both are ambient, and that is worth knowing when reading the output.
if ($Preview) {
    Write-Host "`n=== azd provision --preview ($Environment) - nothing will be created ===`n"
    azd provision --preview
} else {
    Write-Host "`n=== azd provision ($Environment) - this creates real resources ===`n"
    azd provision
}

exit $LASTEXITCODE
