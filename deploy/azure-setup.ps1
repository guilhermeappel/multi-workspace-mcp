#Requires -Version 7.0
<#
.SYNOPSIS
  Creates (once) the Azure Container Apps infrastructure for multi-workspace-mcp.

.DESCRIPTION
  Asks in the terminal for the Notion tokens and the password (nothing is saved to a file) and, at the end,
  prints the URL to paste into Claude. The values are passed to `az` as arguments, so they are visible to
  other processes of your own user for the few seconds each command runs.

.PARAMETER Image
  The image to run. Use a sha-xxxxxxx tag from the CI job summary: Container Apps does not pull a mutable tag
  such as "latest" again, so it would never update.

.PARAMETER RegistryUsername
  Only for a private image: the GitHub user that owns the package. The script then asks for a classic personal
  access token with the read:packages scope. Leave it out when the GHCR package is public.

.PARAMETER EnableLogAnalytics
  Keeps the logs (30 days) in a Log Analytics workspace with a daily ingestion cap, so failed logins stay
  searchable. Off by default: without it logs only exist in the live stream (az containerapp logs show).

.PARAMETER DryRun
  Prints every az command with the secrets hidden instead of running it.

.EXAMPLE
  ./deploy/azure-setup.ps1 -Image ghcr.io/<owner>/multi-workspace-mcp:sha-xxxxxxx

.EXAMPLE
  ./deploy/azure-setup.ps1 -Image ghcr.io/<owner>/multi-workspace-mcp:sha-xxxxxxx -RegistryUsername <owner> -EnableLogAnalytics
#>
param(
    [Parameter(Mandatory)] [string] $Image,
    [string] $RegistryUsername,
    [string[]] $Workspaces = @("work", "personal"),
    [string] $ResourceGroup = "rg-notion-mcp",
    [string] $Location = "eastus",
    [string] $EnvironmentName = "cae-notion-mcp",
    [string] $AppName = "notion-mcp",
    [switch] $EnableLogAnalytics,
    [string] $LogWorkspaceName = "law-notion-mcp",
    [double] $LogDailyCapGb = 0.1,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$MinPasswordLength = 16

function Assert-Az([string] $step) {
    if ($LASTEXITCODE -ne 0) { throw "Failed: $step (exit $LASTEXITCODE)" }
}

# Shows an az command without the values of the arguments that carry secrets.
function Format-Redacted([string[]] $AzArgs) {
    $secretFlags = @("--registry-password", "--logs-workspace-key")
    $shown = @()
    $inSecrets = $false
    for ($i = 0; $i -lt $AzArgs.Count; $i++) {
        $current = $AzArgs[$i]
        if ($current.StartsWith("--")) { $inSecrets = ($current -eq "--secrets") }
        if ($i -gt 0 -and $secretFlags -contains $AzArgs[$i - 1]) { $shown += "***" }
        elseif ($inSecrets -and -not $current.StartsWith("--")) { $shown += (($current -split "=", 2)[0] + "=***") }
        else { $shown += $current }
    }
    return ($shown -join " ")
}

# Runs `az` (or, with -DryRun, only prints it). Takes the whole argument list as one array so lists such as
# --secrets are flattened explicitly. Output lines come back to the caller.
function Invoke-Az([string[]] $AzArgs) {
    if ($DryRun) {
        Write-Host "[dry run] az $(Format-Redacted $AzArgs)"
        $global:LASTEXITCODE = 0
        return
    }

    & az @AzArgs
}

if ($Image -notmatch ":" -or $Image -match ":latest$") {
    Write-Warning "Use a sha-xxxxxxx tag: Container Apps does not pull a mutable tag such as 'latest' again."
}

Write-Host "==> Checking Azure login"
Invoke-Az @("account", "show", "--query", "{subscription:name, id:id}", "--output", "table")
Assert-Az "az account show (run 'az login' first)"

Write-Host "==> containerapp commands and providers"
# Recent Azure CLI versions ship `az containerapp` built in. Install the (preview) extension only when the
# commands are missing: its pip install can fail on a machine without a matching build toolchain.
if ($DryRun) {
    Write-Host "[dry run] az extension add --name containerapp   (only if 'az containerapp' is not built in)"
}
else {
    & az containerapp create --help *> $null
    if ($LASTEXITCODE -ne 0) {
        Invoke-Az @("extension", "add", "--name", "containerapp", "--upgrade", "--only-show-errors")
        Assert-Az "extension add"
    }
}
Invoke-Az @("provider", "register", "--namespace", "Microsoft.App", "--wait")
Assert-Az "provider Microsoft.App"
# Container Apps needs both providers registered, even without Log Analytics.
Invoke-Az @("provider", "register", "--namespace", "Microsoft.OperationalInsights", "--wait")
Assert-Az "provider Microsoft.OperationalInsights"

# ---------- Secrets (typed in, never written to disk) ----------
$registryArgs = @()
if ($RegistryUsername) {
    $registryPassword = Read-Host "GitHub classic PAT with read:packages scope (to pull the private image)" -MaskInput
    if ([string]::IsNullOrWhiteSpace($registryPassword)) { throw "Empty PAT." }
    $registryArgs = @("--registry-server", "ghcr.io", "--registry-username", $RegistryUsername, "--registry-password", $registryPassword)
}
else {
    Write-Host "No -RegistryUsername: the image is assumed to be public, so no registry credentials are set."
}

$secrets = @()
$envVars = @()

foreach ($ws in $Workspaces) {
    $name = $ws.ToLowerInvariant()
    if ($name -notmatch '^[a-z0-9][a-z0-9-]*$') { throw "Invalid workspace name '$ws': use lowercase letters, digits and hyphens." }
    $token = Read-Host "Notion integration token for '$name' (ntn_...)" -MaskInput
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Empty token for '$name'." }
    if ($token -notmatch '^(ntn_|secret_)') { Write-Warning "That does not look like a Notion integration token (it should start with ntn_)." }
    $secretName = "notion-$name"
    # Notion:Workspaces:<name> as an environment variable; the server turns "_" back into "-".
    $envName = "Notion__Workspaces__" + $name.Replace("-", "_")
    $secrets += "$secretName=$token"
    $envVars += "$envName=secretref:$secretName"
}

$password = Read-Host "Password to authorize the connector (min. $MinPasswordLength characters, no spaces, quotes or commas)" -MaskInput
if ($password.Length -lt $MinPasswordLength) { throw "The password needs at least $MinPasswordLength characters." }
if ($password -match '[\s"''`,]') { throw "The password must not contain spaces, quotes, backticks or commas (they break the az --secrets syntax)." }

# 256-bit HMAC key as hex (no '=' so it does not confuse the --secrets parser).
$signingKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

$secrets += "auth-password=$password", "signing-key=$signingKey"
$envVars += "Server__AuthPassword=secretref:auth-password", "Server__SigningKey=secretref:signing-key", "Server__TokenGeneration=1"

# ---------- Infra ----------
Write-Host "==> Resource group $ResourceGroup ($Location)"
Invoke-Az @("group", "create", "--name", $ResourceGroup, "--location", $Location, "--output", "none")
Assert-Az "group create"

$logsArgs = @("--logs-destination", "none")
if ($EnableLogAnalytics) {
    $cap = $LogDailyCapGb.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    Write-Host "==> Log Analytics workspace $LogWorkspaceName (30 days, daily cap $cap GB)"
    Invoke-Az @("monitor", "log-analytics", "workspace", "create", "--resource-group", $ResourceGroup, "--workspace-name", $LogWorkspaceName,
        "--location", $Location, "--quota", $cap, "--retention-time", "30", "--output", "none")
    Assert-Az "log-analytics workspace create"

    $workspaceId = Invoke-Az @("monitor", "log-analytics", "workspace", "show", "--resource-group", $ResourceGroup, "--workspace-name", $LogWorkspaceName,
        "--query", "customerId", "--output", "tsv")
    Assert-Az "log-analytics workspace show"
    $workspaceKey = Invoke-Az @("monitor", "log-analytics", "workspace", "get-shared-keys", "--resource-group", $ResourceGroup, "--workspace-name", $LogWorkspaceName,
        "--query", "primarySharedKey", "--output", "tsv")
    Assert-Az "log-analytics get-shared-keys"
    if ($DryRun) { $workspaceId = "<workspace-id>"; $workspaceKey = "<workspace-key>" }

    $logsArgs = @("--logs-destination", "log-analytics", "--logs-workspace-id", $workspaceId, "--logs-workspace-key", $workspaceKey)
}
else {
    Write-Host "==> (no Log Analytics: logs only exist in the live stream; use -EnableLogAnalytics to keep them)"
}

Write-Host "==> Container Apps environment"
Invoke-Az (@("containerapp", "env", "create", "--name", $EnvironmentName, "--resource-group", $ResourceGroup, "--location", $Location) + $logsArgs + @("--output", "none"))
Assert-Az "containerapp env create"

Write-Host "==> Container App (scales to zero, 0.25 vCPU / 0.5 GiB, one replica at most)"
Invoke-Az (@("containerapp", "create", "--name", $AppName, "--resource-group", $ResourceGroup, "--environment", $EnvironmentName,
        "--image", $Image) + $registryArgs + @("--target-port", "8080", "--ingress", "external",
        "--min-replicas", "0", "--max-replicas", "1", "--cpu", "0.25", "--memory", "0.5Gi",
        "--secrets") + $secrets + @("--env-vars") + $envVars + @("--output", "none"))
Assert-Az "containerapp create"

$fqdn = Invoke-Az @("containerapp", "show", "--name", $AppName, "--resource-group", $ResourceGroup,
    "--query", "properties.configuration.ingress.fqdn", "--output", "tsv")
Assert-Az "containerapp show"
if ($DryRun) { $fqdn = "<app>.<region>.azurecontainerapps.io" }

Write-Host "==> Setting Server__PublicBaseUrl=https://$fqdn"
Invoke-Az @("containerapp", "update", "--name", $AppName, "--resource-group", $ResourceGroup,
    "--set-env-vars", "Server__PublicBaseUrl=https://$fqdn", "--output", "none")
Assert-Az "containerapp update"

Write-Host ""
Write-Host "Done! Test:  https://$fqdn/healthz"
Write-Host "Connector URL for Claude:  https://$fqdn/mcp" -ForegroundColor Green
Write-Host "Keep the password you typed; it is asked when Claude connects."