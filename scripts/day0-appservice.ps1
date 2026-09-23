# Day-0 deploy of Kvytok.Api to Azure App Service (Linux, F1).
# Usage: ./scripts/day0-appservice.ps1 [-AppName app-kvytok-dev-01] [-Location swedencentral]

param(
    [string]$ResourceGroup = "rg-kvytok-dev-001",
    [string]$Location      = "swedencentral",
    [string]$Plan          = "asp-kvytok-dev-001",
    [string]$AppName       = "app-kvytok-dev-01",
    [string]$Runtime       = "DOTNETCORE:10.0"
)

$ErrorActionPreference = "Stop"

function Invoke-Az {
    az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

$root       = Split-Path $PSScriptRoot -Parent
$project    = Join-Path $root "src/Kvytok.Api/Kvytok.Api.csproj"
$publishDir = Join-Path $root "publish"
$zip        = Join-Path $root "publish.zip"

# The group is created only once (re-creating it in another region fails); the plan gets an
# explicit location so it does not silently inherit the group's region.
if ((az group exists -n $ResourceGroup) -ne "true") {
    Invoke-Az group create -n $ResourceGroup -l $Location -o none
}
Invoke-Az appservice plan create -g $ResourceGroup -n $Plan -l $Location --sku F1 --is-linux -o none

$existing = az webapp list -g $ResourceGroup --query "[?name=='$AppName'].name" -o tsv
if (-not $existing) {
    Invoke-Az webapp create -g $ResourceGroup -n $AppName -p $Plan --runtime $Runtime -o none
}

dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -Force
Invoke-Az webapp deploy -g $ResourceGroup -n $AppName --src-path $zip --type zip

Write-Host "Deployed: https://$AppName.azurewebsites.net"
