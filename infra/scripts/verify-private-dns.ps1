<#
.SYNOPSIS
Day 27 - proves the data-tier private endpoints actually resolve privately,
instead of trusting that a NIC existing means DNS points at it.

.DESCRIPTION
Resolving these FQDNs from a laptop or a GitHub Actions runner returns the
*public* addresses, correctly - DNS from outside the VNet was never supposed
to change, and a lookup from outside proves nothing. The private path is only
real if three things are all true at once, and each of them can be wrong on
its own while every resource still deploys green:

  1. The private endpoint exists and holds a private IP.
  2. The private DNS zone holds an A record for that exact hostname pointing
     at that IP. (A missing privateDnsZoneGroup leaves the zone empty; a zone
     created under a slightly wrong name is never populated and Azure never
     complains.)
  3. That zone is linked to the VNet. (An unlinked zone is invisible to every
     client in it, and nothing about the endpoint reports this.)

Azure's VNet resolver serves a linked zone's records to every client in that
VNet, so 1 + 2 + 3 together are what "it resolves privately" means. This
script asserts all three from Azure's own state, which needs no compute and
takes seconds.

-WithContainerLookup additionally runs a real nslookup from inside the VNet,
using a one-shot container in the delegated verification subnet. That is the
end-to-end version of the same proof, and it is opt-in because it depends on
the subscription having Microsoft.ContainerInstance registered and on the
container image being pullable from where ACI runs - neither of which is
true by default, and neither of which says anything about whether the
private endpoints are correct.

.PARAMETER Environment
dev or prod - selects which azd environment's stack outputs to read.

.PARAMETER WithContainerLookup
Also run a live nslookup from a throwaway container inside the VNet.

.PARAMETER ContainerImage
Image for that container. Needs nslookup on PATH. Defaults to busybox from
Docker Hub, which ACI pulls anonymously and which Docker Hub rate-limits -
override it with an image from a registry that does not (an ACR, or an
mcr.microsoft.com image) if that pull fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [switch]$WithContainerLookup,

    [string]$ContainerImage = 'busybox'
)

$ErrorActionPreference = 'Stop'

function Get-StackOutputs {
    param([string]$StackName)
    # Flattened, not nested under "properties" - confirmed against this
    # repo's own CI verification step, which queries provisioningState and
    # systemData.lastModifiedAt the same way. Querying "properties.outputs"
    # here returns nothing and looks identical to "the stack doesn't exist."
    $json = az stack sub show --name $StackName --query outputs -o json
    if (-not $json) {
        throw "No stack named '$StackName', or it has no outputs. Has '$Environment' been provisioned since enablePrivateEndpoints was added?"
    }
    return $json | ConvertFrom-Json
}

# --os-type, --cpu and --memory are all stated explicitly because a
# VNet-attached container group does not get az CLI's client-side defaults for
# them - ARM rejects the payload field by field (InvalidOsType, then
# ResourceRequestsNotSpecified) rather than defaulting anything. A non-VNet
# `az container create` with the same arguments needs none of these.
function Resolve-FromInsideVnet {
    param(
        [string]$ResourceGroup,
        [string]$SubnetId,
        [string]$Fqdn,
        [string]$ContainerName,
        [string]$Image
    )

    # One target per container, running a bare two-word command, rather than
    # several joined with semicolons inside one shell string: az's Windows
    # wrapper mangles embedded quotes and semicolons in --command-line, and
    # `nslookup <fqdn>` needs no quoting at all.
    az container create `
        --resource-group $ResourceGroup `
        --name $ContainerName `
        --image $Image `
        --os-type Linux `
        --cpu 1 `
        --memory 1 `
        --subnet $SubnetId `
        --restart-policy Never `
        --command-line "nslookup $Fqdn" `
        --no-wait `
        --output none

    if ($LASTEXITCODE -ne 0) {
        throw "az container create failed for '$ContainerName' (exit $LASTEXITCODE). See the error above. If it is a registry error, pass -ContainerImage with an image from a registry that does not rate-limit anonymous pulls."
    }

    try {
        $state = ''
        for ($i = 0; $i -lt 24; $i++) {
            Start-Sleep -Seconds 5
            $state = az container show -g $ResourceGroup -n $ContainerName --query "instanceView.state" -o tsv 2>$null
            if ($state -in @('Succeeded', 'Failed', 'Terminated')) { break }
        }
        if ($state -notin @('Succeeded', 'Failed', 'Terminated')) {
            Write-Warning "  Container did not reach a terminal state within 2 minutes (last seen: '$state'). Reading logs anyway."
        }
        return az container logs -g $ResourceGroup -n $ContainerName 2>$null
    }
    finally {
        az container delete -g $ResourceGroup -n $ContainerName --yes --output none 2>$null
    }
}

$stackName = "azd-stack-$Environment"
Write-Host "Reading outputs from $stackName..."
$outputs = Get-StackOutputs -StackName $stackName

if ($outputs.privateEndpointsEnabled.value -ne $true) {
    throw "privateEndpointsEnabled is false on the last deploy of '$Environment'. Re-provision with it on before verifying."
}

$resourceGroup = $outputs.resourceGroupName.value
$vnetId = $outputs.privateEndpointVnetId.value

$targets = @(
    [pscustomobject]@{
        Label      = 'SQL server'
        Fqdn       = $outputs.sqlServerFqdn.value
        ExpectedIp = $outputs.sqlPrivateEndpointIp.value
        Zone       = 'privatelink.database.windows.net'
    }
    [pscustomobject]@{
        Label      = 'Key Vault'
        Fqdn       = ([Uri]$outputs.keyVaultUri.value).Host
        ExpectedIp = $outputs.keyVaultPrivateEndpointIp.value
        Zone       = 'privatelink.vaultcore.azure.net'
    }
)
if (-not [string]::IsNullOrWhiteSpace($outputs.serviceBusPrivateEndpointIp.value)) {
    $targets += [pscustomobject]@{
        Label      = 'Service Bus'
        Fqdn       = $outputs.serviceBusFqdn.value
        ExpectedIp = $outputs.serviceBusPrivateEndpointIp.value
        Zone       = 'privatelink.servicebus.windows.net'
    }
}

Write-Host ""
Write-Host "Private endpoints to prove, in ${resourceGroup}:"
$targets | ForEach-Object { Write-Host "  $($_.Label): $($_.Fqdn) -> expect $($_.ExpectedIp) (zone $($_.Zone))" }
Write-Host ""

$failed = $false

foreach ($t in $targets) {
    Write-Host "=== $($t.Label) - $($t.Fqdn) ==="

    if ([string]::IsNullOrWhiteSpace($t.ExpectedIp)) {
        Write-Host "  FAIL: the stack reports no private IP for this endpoint."
        $failed = $true
        Write-Host ""
        continue
    }
    Write-Host "  endpoint IP (from the stack): $($t.ExpectedIp)"

    # 2. the zone holds the A record, under the hostname's first label
    $recordName = $t.Fqdn.Split('.')[0]
    $recordIps = az network private-dns record-set a show `
        --resource-group $resourceGroup `
        --zone-name $t.Zone `
        --name $recordName `
        --query "aRecords[].ipv4Address" -o tsv 2>$null

    if ([string]::IsNullOrWhiteSpace($recordIps)) {
        Write-Host "  FAIL: zone $($t.Zone) has no A record named '$recordName'. The privateDnsZoneGroup did not register one."
        $failed = $true
    }
    elseif ($recordIps -split '\s+' -contains $t.ExpectedIp) {
        Write-Host "  PASS: $recordName.$($t.Zone) A -> $recordIps"
    }
    else {
        Write-Host "  FAIL: zone record points at '$recordIps', not at the endpoint's $($t.ExpectedIp)."
        $failed = $true
    }

    # 3. the zone is linked to this VNet, or nothing in it can see the record
    $linkedVnets = az network private-dns link vnet list `
        --resource-group $resourceGroup `
        --zone-name $t.Zone `
        --query "[?virtualNetworkLinkState=='Completed'].virtualNetwork.id" -o tsv 2>$null

    if ($linkedVnets -and ($linkedVnets -split '\s+' -contains $vnetId)) {
        Write-Host "  PASS: zone is linked to the private-endpoint VNet."
    }
    else {
        Write-Host "  FAIL: zone $($t.Zone) is not linked to $vnetId - every client in that VNet would still get the public address."
        $failed = $true
    }

    Write-Host ""
}

if ($WithContainerLookup) {
    $subnetId = $outputs.privateEndpointVerificationSubnetId.value
    if ([string]::IsNullOrWhiteSpace($subnetId)) {
        throw "No verification subnet in the stack outputs, so -WithContainerLookup has nowhere to run."
    }

    Write-Host "=== live lookups from inside the VNet ==="
    $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $i = 0
    foreach ($t in $targets) {
        $i++
        $aciName = "verify-dns-$Environment-$stamp-$i"
        Write-Host "Looking up $($t.Fqdn) (container '$aciName', image '$ContainerImage')..."
        $log = Resolve-FromInsideVnet -ResourceGroup $resourceGroup -SubnetId $subnetId -Fqdn $t.Fqdn -ContainerName $aciName -Image $ContainerImage
        Write-Host "--- nslookup output ---"
        Write-Host $log
        Write-Host "-----------------------"
        if ($log -match [regex]::Escape($t.ExpectedIp)) {
            Write-Host "  PASS: resolved to $($t.ExpectedIp) from inside the VNet."
        }
        else {
            Write-Host "  FAIL: did not resolve to $($t.ExpectedIp) from inside the VNet."
            $failed = $true
        }
        Write-Host ""
    }
}

if ($failed) {
    throw "Private DNS verification failed for '$Environment'. See the failures above."
}

Write-Host "All private endpoints resolve privately inside $vnetId."
if (-not $WithContainerLookup) {
    Write-Host "(Zone records and VNet links asserted from Azure's own state. Add -WithContainerLookup for a live nslookup from inside the VNet.)"
}
