<#
Day 24 - the entry point for provisioning this stack through azd.

Wraps `azd provision` with the one step azd cannot do for itself: putting the
right main.bicepparam on disk *before* azd reads parameters. See
scripts/select-bicepparam.ps1 for why that cannot be a preprovision hook.

    ./scripts/azd-provision.ps1 -Environment dev -Preview   # plan, changes nothing
    ./scripts/azd-provision.ps1 -Environment dev            # deploy for real
    ./scripts/azd-provision.ps1 -Environment prod -Preview  # plan prod

    # deploy dev into the one managed environment this subscription allows:
    ./scripts/azd-provision.ps1 -Environment dev -ReuseManagedEnvironment

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
    [switch]$Preview,

    # Discover the managed environment that already exists in this subscription
    # and deploy the container app into it instead of creating a second one.
    #
    # Needed because the subscription caps at one managed environment and the
    # live quotes-api holds it, so a deployment that creates its own fails at
    # MaxNumberOfGlobalEnvironmentsInSubExceeded with SQL and Service Bus
    # already standing (Days/day-24, Finding 4). This is a switch rather than
    # the default because "borrow whatever environment happens to be lying
    # around" is the wrong behaviour for a subscription with room, and silently
    # right behaviour is how a stack ends up depending on infrastructure nobody
    # meant to share.
    [switch]$ReuseManagedEnvironment
)

$ErrorActionPreference = 'Stop'

# Every native call below is checked by hand via $LASTEXITCODE, with a throw
# that says what to do about it. PowerShell 7.4 turned that pattern off by
# default: $PSNativeCommandUseErrorActionPreference became $true, so a non-zero
# exit from az or azd raises a terminating error *before* the next line runs -
# which makes those throws dead code, replaces their guidance with azd's raw
# stderr, and turns the two intentionally-tolerant calls in the else branch
# below into hard failures. Opting out restores the checks as written. On PS
# 5.1 the variable simply does not exist, hence the guarded assignment.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

<#
Runs a native command whose output this script captures or discards, and
returns its stdout lines. $LASTEXITCODE is left intact for the caller to check.

This exists because of a Windows PowerShell 5.1 behaviour that is easy to get
backwards, and this script got it backwards once already. In 5.1, redirecting a
native command's stderr - `2>$null` included - wraps that stderr in an
ErrorRecord, and an ErrorRecord under $ErrorActionPreference = 'Stop' is
terminating. So `azd env get-value X 2>$null` does not quietly ignore azd's
chatter; it converts that chatter into a fatal error. azd writes "Update
available: ..." to stderr on *every* invocation, which made the redirection a
coin flip on whether a new azd release had shipped - and it landed the wrong
way on 1.31.1 -> 1.33.0, killing the run after both azd variables had already
been written.

Leaving stderr unredirected would also work on 5.1 (it prints to the console
and creates no error record), but then azd's update banner and any real
diagnostic are indistinguishable from this script's own output. Relaxing the
preference for the duration of the call is the fix that keeps both properties:
stderr stays suppressed, and suppressing it is not fatal. PowerShell 7 does not
need this, and is unharmed by it.
#>
function Invoke-NativeCapture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock]$Command)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Command 2>$null
    } finally {
        $ErrorActionPreference = $previous
    }

    # Native output arrives as lines; blank ones carry no information and a
    # trailing newline is normal, so they are dropped rather than returned as
    # an empty-string "value" a caller would treat as set.
    return @($output | Where-Object { "$_".Trim() -ne '' })
}

# `azd env get-value` for a key that is not set exits non-zero. That is not an
# error at either call site below - both are asking "is this set?" - so the
# exit code becomes $null rather than a throw.
function Get-AzdValue {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Key)

    $lines = Invoke-NativeCapture { azd env get-value $Key }
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($lines | Select-Object -Last 1)
}

# 1. Parameters file first - before azd is invoked at all.
& (Join-Path $PSScriptRoot 'select-bicepparam.ps1') -EnvironmentName $Environment

# 2. Make sure azd is pointed at the matching environment. `azd env select`
#    fails loudly if the environment does not exist yet, which is the correct
#    behaviour: creating it silently here would hide a typo'd -Environment
#    behind a brand-new, empty environment.
azd env select $Environment
if ($LASTEXITCODE -ne 0) {
    throw "azd env select '$Environment' failed. Create it first: azd env new $Environment --location centralindia"
}

# 3. If borrowing an environment, find it now and hand azd the two values the
#    parameter files read. Discovered rather than pasted: the ID carries a
#    subscription ID, and the region has to match the environment's own or the
#    container app is rejected at deploy time - two facts that should come from
#    Azure, not from a comment that was true once.
if ($ReuseManagedEnvironment) {
    Write-Host "`n=== discovering an existing Container Apps managed environment ===`n"

    # Pinned to the subscription azd will actually deploy into, not whatever
    # `az account` happens to have selected. A container app's environment must
    # be in the app's own subscription, so a mismatch here produces a
    # perfectly well-formed ID that fails at deploy time - and azd already
    # knows the right answer.
    $subscriptionId = Get-AzdValue AZURE_SUBSCRIPTION_ID
    if (-not $subscriptionId) {
        throw "Could not read AZURE_SUBSCRIPTION_ID from azd environment '$Environment'. Set it with: azd env set AZURE_SUBSCRIPTION_ID <id>"
    }

    # No provisioningState filter. An environment mid-platform-upgrade reports
    # something other than Succeeded and still consumes the one-per-subscription
    # quota, so filtering it out would produce the least useful possible
    # outcome: "nothing to reuse, drop the switch" followed by
    # MaxNumberOfGlobalEnvironmentsInSubExceeded. State is reported instead of
    # used as a filter.
    #
    # @(...) is load-bearing. ConvertFrom-Json unwraps a one-element JSON array
    # to a bare object on Windows PowerShell 5.1, which would make .Count $null
    # and $existing[0] fail - on the single-environment case that is the whole
    # point of this switch. Forcing an array makes the count checks below mean
    # what they say regardless of which PowerShell is running the script.
    $envJson = Invoke-NativeCapture { az containerapp env list --subscription $subscriptionId --query "[].{id:id,location:location,name:name,state:properties.provisioningState}" -o json }
    if ($LASTEXITCODE -ne 0) {
        throw "az containerapp env list failed for subscription $subscriptionId. Is the Azure CLI logged in (az account show)?"
    }
    $existing = @(($envJson -join "`n") | ConvertFrom-Json)
    if ($existing.Count -eq 0) {
        throw "-ReuseManagedEnvironment was passed but this subscription has no managed environment to reuse. Drop the switch and let the stack create its own."
    }
    if ($existing.Count -gt 1) {
        # Not a real state on this subscription - it is capped at one - but an
        # arbitrary pick from several is exactly the kind of decision a script
        # should refuse to make on someone's behalf.
        #
        # The remediation names both variables, not just the ID: setting the ID
        # alone leaves API_LOCATION empty, the app deploys in the stack's region,
        # and Azure rejects it for not matching its environment's - the exact
        # failure this whole block exists to prevent.
        #
        # Built as two statements because `+` binds tighter than `-join` in
        # PowerShell, so the one-expression version joins the *result* of
        # concatenating a string onto an array. It reads fine and is not what it
        # says.
        $options = ($existing | ForEach-Object {
            "  azd env set EXISTING_CONTAINERAPP_ENV_ID $($_.id)$([Environment]::NewLine)  azd env set API_LOCATION $($_.location)"
        }) -join ([Environment]::NewLine * 2)
        throw "This subscription has $($existing.Count) managed environments; refusing to guess which one this stack should join. Set both values explicitly, using one of these pairs:$([Environment]::NewLine)$options"
    }

    $env0 = $existing[0]

    # The one environment must not live in the resource group this stack owns.
    # denySettings and actionOnUnmanage.resources cannot touch a resource the
    # stack does not manage - but azure.yaml also sets
    # actionOnUnmanage.resourceGroups: delete, and deleting a resource group is
    # not resource-scoped: it takes unmanaged contents with it. So the one
    # arrangement where `azd down` would destroy the environment it borrowed is
    # the one where that environment sits inside the stack's own group, and
    # being unmanaged is precisely why nothing would stop it. Today the live
    # environment is in a different group; that is worth asserting rather than
    # relying on.
    $stackResourceGroup = (Select-String -Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'main.bicepparam') -Pattern "^param resourceGroupName\s*=\s*'([^']+)'").Matches.Groups[1].Value
    if ($stackResourceGroup -and $env0.id -match "/resourceGroups/$([regex]::Escape($stackResourceGroup))/") {
        throw "The managed environment to reuse ($($env0.name)) is inside '$stackResourceGroup', which is this stack's own resource group - `azd down` deletes that group and would take the environment with it even though the stack never managed it. Move the environment, or deploy this stack into a different resource group."
    }

    Write-Host "reusing '$($env0.name)' in $($env0.location) [state: $($env0.state)]"
    Write-Host "  $($env0.id)"
    if ($env0.state -ne 'Succeeded') {
        Write-Warning "That environment reports provisioningState '$($env0.state)'. It still occupies this subscription's single environment slot, so it is the one to join, but the deployment may fail until it settles."
    }

    azd env set EXISTING_CONTAINERAPP_ENV_ID $env0.id
    if ($LASTEXITCODE -ne 0) { throw "azd env set EXISTING_CONTAINERAPP_ENV_ID failed." }

    # The app follows the environment's region; the rest of the stack keeps
    # AZURE_LOCATION. They differ here on purpose - see main.bicep's apiLocation.
    azd env set API_LOCATION $env0.location
    if ($LASTEXITCODE -ne 0) { throw "azd env set API_LOCATION failed." }

    # Absent AZURE_LOCATION is not an error - `azd env new` without --location
    # leaves it unset - so the exit code is read and discarded rather than
    # checked. This whole block is a note to the operator, not a gate.
    $stackLocation = Get-AzdValue AZURE_LOCATION
    if ($stackLocation -and $stackLocation -ne $env0.location) {
        Write-Host "`nnote: container app -> $($env0.location) (its environment's region), everything else -> $stackLocation."
        Write-Host "      that cross-region hop from app to SQL is deliberate and documented; southindia cannot host a new SQL server on this subscription."
    }
} else {
    # Going the other way is not symmetrical, and this is the only place that
    # can say so. A container app's `location` and `environmentId` are both
    # immutable: once this environment has been deployed with a borrowed
    # environment, a run without the switch asks Azure to move the same app to
    # a different region and a different environment, and ARM answers with
    # "already exists in location ..." rather than doing it. Recreating is the
    # only route, and azure.yaml's denyDelete blocks the delete half of that.
    # So: warn loudly, do not silently attempt it.
    $priorEnvId = Get-AzdValue EXISTING_CONTAINERAPP_ENV_ID
    if ($priorEnvId) {
        Write-Warning "This environment was last provisioned with -ReuseManagedEnvironment. Dropping the switch changes the container app's environmentId and region, both immutable - expect ARM to refuse rather than migrate. Re-run with -ReuseManagedEnvironment, or tear down first with: azd down --force --purge"
    }

    # Clear them, so a previous -ReuseManagedEnvironment run cannot leak into a
    # later one that meant to create its own environment. Same class of bug as
    # Finding 5's stale main.bicepparam, and worth closing the same way.
    # Exit codes deliberately not checked here: an empty value is equivalent
    # to unset as far as the parameter files are concerned
    # (`readEnvironmentVariable(..., '')`), so if a given azd build refuses to
    # store one, the fallback is already the behaviour this wants.
    Invoke-NativeCapture { azd env set EXISTING_CONTAINERAPP_ENV_ID "" } | Out-Null
    Invoke-NativeCapture { azd env set API_LOCATION "" } | Out-Null
}

# 4. Provision. Deployment Stacks come from azure.yaml's infra.deploymentStacks
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
