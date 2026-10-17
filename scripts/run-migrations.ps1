# Starts the migrator Container Apps job and waits for it, so a failed migration fails the deploy.
# Runs as the azd postdeploy hook (see azure.yaml); azd supplies AZURE_ENV_NAME and AZURE_SUBSCRIPTION_ID.
# Requires the Azure CLI signed in to the same subscription as azd (az login is separate from azd auth login).

param(
    [string]$ResourceGroup  = ($env:AZURE_RESOURCE_GROUP ? $env:AZURE_RESOURCE_GROUP : "rg-$env:AZURE_ENV_NAME"),
    [string]$Subscription   = $env:AZURE_SUBSCRIPTION_ID,
    [string]$JobName        = "migrator",
    # A little over the job's replicaTimeout (1800 s), after which Container Apps fails it anyway
    [int]$TimeoutSeconds    = 1900,
    [int]$PollSeconds       = 10
)

$ErrorActionPreference = "Stop"

function Invoke-Az {
    $output = az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed with exit code $LASTEXITCODE" }
    $output
}

$common = @("-n", $JobName, "-g", $ResourceGroup)
if ($Subscription) { $common += @("--subscription", $Subscription) }

$execution = Invoke-Az containerapp job start @common --query name -o tsv
Write-Host "Started $JobName execution $execution in $ResourceGroup"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds $PollSeconds
    $status = Invoke-Az containerapp job execution show @common --job-execution-name $execution --query properties.status -o tsv
    Write-Host "  $status"
} while ($status -in @("Running", "Processing", "Unknown") -and (Get-Date) -lt $deadline)

if ($status -ne "Succeeded") {
    # Logs are under the job's Execution history in the portal (az containerapp job logs needs the containerapp extension)
    throw "Migration $execution in $ResourceGroup ended as '$status'"
}
Write-Host "Migrations applied"
