# End-to-end check: Catalog (Postgres + Blob) -> Basket (Redis) -> checkout (Service Bus) -> Order (Postgres).
#   Azure (through APIM):         ./infra/smoke-test.ps1
#   Local docker compose:         ./infra/smoke-test.ps1 -Local
#   Local Visual Studio (F5):     ./infra/smoke-test.ps1 -Local -VisualStudio
# Signed-in checks need an Entra token: taken from the Azure deployment, or pass -Scope. Without one, only anonymous checks run.
param(
    [string]$EnvironmentName = 'myec',
    [switch]$Local,
    [switch]$VisualStudio,
    [string]$Scope,
    [int]$OrderTimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$failures = 0
$skipped = 0

function Step([string]$name, [scriptblock]$body, [switch]$NeedsAuth) {
    Write-Host "`n> $name" -ForegroundColor Cyan
    if ($NeedsAuth -and -not $token) { $script:skipped++; Write-Host "  SKIP (no Entra token)" -ForegroundColor Yellow; return }
    try { & $body; Write-Host "  PASS" -ForegroundColor Green }
    catch { $script:failures++; Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red }
}

# Retries cover cold starts (apps scale to zero in Azure, containers warming up locally).
function Api([string]$method, [string]$service, [string]$path, $body = $null, [switch]$Anonymous, [int]$attempts = 6) {
    $params = @{ Uri = "$($base[$service])$path"; Method = $method; ContentType = 'application/json'; TimeoutSec = 60 }
    if (-not $Anonymous -and $token) { $params.Headers = @{ Authorization = "Bearer $token" } }
    if ($null -ne $body) { $params.Body = ConvertTo-Json $body -Depth 5 -Compress }
    for ($i = 1; ; $i++) {
        try { return Invoke-RestMethod @params }
        catch {
            $status = [int]$_.Exception.Response.StatusCode
            $detail = if ($status) { "$status $($_.ErrorDetails.Message)" } else { $_.Exception.Message }
            if ($i -ge $attempts -or ($status -ge 400 -and $status -lt 500)) { throw "$method $($params.Uri) -> $detail" }
            Start-Sleep -Seconds 10
        }
    }
}

# ---------- Targets and token ----------
$out = $null
if (Get-Command az -ErrorAction SilentlyContinue) {
    $json = az deployment sub show -n "$EnvironmentName-apps" --query properties.outputs -o json 2>$null
    if (-not $LASTEXITCODE) { $out = $json | ConvertFrom-Json }
}

if ($Local) {
    $base = if ($VisualStudio) {
        @{ catalog = 'http://localhost:5263'; basket = 'http://localhost:5183'; order = 'http://localhost:5235' }
    } else {
        @{ catalog = 'http://localhost:5001'; basket = 'http://localhost:5002'; order = 'http://localhost:5003' }
    }
} else {
    if (-not $out.apiGatewayUrl.value) { throw "No '$EnvironmentName-apps' deployment found. Run deploy.ps1 first, or use -Local." }
    $gw = $out.apiGatewayUrl.value
    $base = @{ catalog = "$gw/catalog"; basket = "$gw/basket"; order = "$gw/order" }
}

if (-not $Scope -and $out) {
    # Users get a delegated token; service principals (CI) use the app-only .default scope.
    $Scope = if ((az account show --query user.type -o tsv) -eq 'user') { $out.entraScope.value } else { "$($out.entraClientId.value)/.default" }
}
$token = $null
if ($Scope) {
    $token = az account get-access-token --scope $Scope --query accessToken -o tsv
    if ($LASTEXITCODE) { throw "Could not get a token for $Scope." }
} elseif (-not $Local) {
    throw 'No Entra scope available.'
}
Write-Host "Catalog: $($base.catalog)`nBasket:  $($base.basket)`nOrder:   $($base.order)"

# ---------- Tests ----------
Step 'Health endpoints' {
    if ($Local) { foreach ($s in 'catalog', 'basket', 'order') { Api GET $s '/health' -Anonymous | Out-Null } }
}

Step 'Catalog: anonymous product list' { Api GET catalog '/api/products' -Anonymous | Out-Null }

Step 'Catalog: anonymous create is rejected' {
    try { Invoke-RestMethod "$($base.catalog)/api/products" -Method Post -ContentType application/json -Body '{}' | Out-Null; throw 'expected 401' }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 401) { throw } }
}

Step 'Catalog: admin creates product (PostgreSQL)' -NeedsAuth {
    $script:product = Api POST catalog '/api/products' @{ name = "smoke-$(Get-Date -Format HHmmss)"; price = 20; stock = 5 }
    if (-not $product.id) { throw 'no product id returned' }
}

Step 'Catalog: image upload (Blob Storage / Azurite)' -NeedsAuth {
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
    $file = Join-Path ([IO.Path]::GetTempPath()) 'smoke.png'
    [IO.File]::WriteAllBytes($file, $png)
    $json = curl.exe -sS -f -H "Authorization: Bearer $token" -F "file=@$file;type=image/png" "$($base.catalog)/api/products/$($product.id)/image"
    if ($LASTEXITCODE) { throw "upload failed (curl exit $LASTEXITCODE)" }
    $url = ($json | ConvertFrom-Json).imageUrl
    $downloaded = (Invoke-WebRequest $url -UseBasicParsing).Content
    if ($downloaded.Length -ne $png.Length) { throw "image at $url does not match" }
}

Step 'Basket: save basket (Redis)' -NeedsAuth {
    $basket = Api PUT basket '/api/basket' @(@{ productId = $product.id; productName = $product.name; price = 20; quantity = 2 })
    if ($basket.total -ne 40) { throw "expected total 40, got $($basket.total)" }
}

Step 'Basket: checkout (Service Bus)' -NeedsAuth {
    $script:eventId = (Api POST basket '/api/basket/checkout' @{ shippingAddress = 'Smoke Test St 1' }).eventId
    if (-not $eventId) { throw 'no event id returned' }
}

Step 'Order: order created from queue (PostgreSQL)' -NeedsAuth {
    $deadline = (Get-Date).AddSeconds($OrderTimeoutSeconds)
    do {
        $order = @(Api GET order '/api/orders') | Where-Object id -eq $eventId
        if ($order) { if ($order.total -ne 40) { throw "expected total 40, got $($order.total)" }; return }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    throw "order $eventId not created within $OrderTimeoutSeconds s (check order logs and the queue's dead-letter count)"
}

Step 'Cleanup: delete test product' -NeedsAuth {
    if ($product.id) { Api DELETE catalog "/api/products/$($product.id)" | Out-Null }
}

# ---------- Result ----------
if ($skipped) { Write-Host "`n$skipped check(s) skipped: no Entra token. Pass -Scope or deploy to Azure first." -ForegroundColor Yellow }
if ($failures) { Write-Host "$failures check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All run checks passed." -ForegroundColor Green
