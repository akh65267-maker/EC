# One command deploys everything:  ./infra/deploy.ps1 -AlertEmail you@example.com
# 1) infrastructure  2) build images in ACR  3) container apps + API Management
param(
    [Parameter(Mandatory)][string]$AlertEmail,
    [string]$Location = 'eastus2',
    [string]$EnvironmentName = 'myec',
    [int]$BudgetAmount = 60
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$template = Join-Path $PSScriptRoot 'main.bicep'
$tag = Get-Date -Format 'yyyyMMddHHmmss'

function Invoke-Az { az @args; if ($LASTEXITCODE) { throw "az $($args[0..2] -join ' ') failed" } }

function Deploy([bool]$apps) {
    $out = az deployment sub create --name "$EnvironmentName-$(if ($apps) {'apps'} else {'infra'})" `
        --location $Location --template-file $template `
        --parameters environmentName=$EnvironmentName location=$Location alertEmail=$AlertEmail `
                     budgetAmount=$BudgetAmount deployApps=$($apps.ToString().ToLower()) imageTag=$tag `
        --query properties.outputs -o json
    if ($LASTEXITCODE) { throw 'Deployment failed.' }
    $out | ConvertFrom-Json
}

az account show -o none 2>$null
if ($LASTEXITCODE) { Invoke-Az login -o none }
Invoke-Az bicep upgrade
foreach ($ns in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.Storage', 'Microsoft.Cache',
                'Microsoft.DBforPostgreSQL', 'Microsoft.ServiceBus', 'Microsoft.ApiManagement', 'Microsoft.Insights',
                'Microsoft.OperationalInsights', 'Microsoft.ManagedIdentity', 'Microsoft.Consumption') {
    Invoke-Az provider register --namespace $ns -o none
}

Write-Host "`n[1/3] Deploying infrastructure..." -ForegroundColor Cyan
$infra = Deploy $false
$acr = $infra.acrName.value

Write-Host "`n[2/3] Building images (tag $tag)..." -ForegroundColor Cyan
foreach ($svc in 'Catalog', 'Basket', 'Order') {
    $image = "$($svc.ToLower()):$tag"
    # Builds in the cloud (no local Docker needed). Falls back to local Docker if ACR Tasks is unavailable.
    az acr build -r $acr -t $image -f "$root/$svc.API/Dockerfile" $root --no-logs
    if ($LASTEXITCODE) {
        Write-Warning "ACR build unavailable, using local Docker for $svc."
        Invoke-Az acr login -n $acr
        $full = "$($infra.acrLoginServer.value)/$image"
        docker build -t $full -f "$root/$svc.API/Dockerfile" $root; if ($LASTEXITCODE) { throw 'docker build failed' }
        docker push $full; if ($LASTEXITCODE) { throw 'docker push failed' }
    }
}

Write-Host "`n[3/3] Deploying container apps and API Management..." -ForegroundColor Cyan
$o = Deploy $true

Write-Host "`nDone." -ForegroundColor Green
Write-Host "API gateway : $($o.apiGatewayUrl.value)"
Write-Host "Get a token : az account get-access-token --scope $($o.entraScope.value) --query accessToken -o tsv"
Write-Host "Try it      : GET $($o.apiGatewayUrl.value)/catalog/api/products"
