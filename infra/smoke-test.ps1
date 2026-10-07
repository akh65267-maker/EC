# End-to-end check of a deployed environment:  ./infra/smoke-test.ps1
# Catalog (Postgres + Blob) -> Basket (Redis) -> checkout (Service Bus) -> Order (Postgres), all through APIM.
param(
    [string]$EnvironmentName = 'myec',
    [int]$OrderTimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$failures = 0

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n> $name" -ForegroundColor Cyan
    try { & $body; Write-Host "  PASS" -ForegroundColor Green }
    catch { $script:failures++; Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red }
}

# Retries cover cold starts: the container apps scale to zero.
function Api([string]$method, [string]$path, $body = $null, [int]$attempts = 6) {
    $params = @{ Uri = "$gw$path"; Method = $method; Headers = $headers; ContentType = 'application/json'; TimeoutSec = 60 }
    if ($null -ne $body) { $params.Body = ConvertTo-Json $body -Depth 5 -Compress }
    for ($i = 1; ; $i++) {
        try { return Invoke-RestMethod @params }
        catch {
            $status = [int]$_.Exception.Response.StatusCode
            if ($i -ge $attempts -or ($status -ge 400 -and $status -lt 500)) { throw "$method $path -> $status $($_.ErrorDetails.Message)" }
            Start-Sleep -Seconds 10
        }
    }
}

# ---------- Read deployment outputs ----------
$out = az deployment sub show -n "$EnvironmentName-apps" --query properties.outputs -o json | ConvertFrom-Json
if ($LASTEXITCODE -or -not $out.apiGatewayUrl.value) { throw "No '$EnvironmentName-apps' deployment found. Run deploy.ps1 first." }
$gw = $out.apiGatewayUrl.value

# Users get a delegated token; service principals (CI) use the app-only .default scope.
$scope = if ((az account show --query user.type -o tsv) -eq 'user') { $out.entraScope.value } else { "$($out.entraClientId.value)/.default" }
$token = az account get-access-token --scope $scope --query accessToken -o tsv
if ($LASTEXITCODE) { throw "Could not get a token for $scope." }
$headers = @{ Authorization = "Bearer $token" }
Write-Host "Gateway: $gw"

# ---------- Tests ----------
Step 'Catalog: anonymous product list' {
    $headersBackup = $headers; $script:headers = @{}
    try { Api GET '/catalog/api/products' | Out-Null } finally { $script:headers = $headersBackup }
}

Step 'Catalog: anonymous create is rejected' {
    try { Invoke-RestMethod "$gw/catalog/api/products" -Method Post -ContentType application/json -Body '{}' | Out-Null; throw 'expected 401' }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 401) { throw } }
}

Step 'Catalog: admin creates product (PostgreSQL)' {
    $script:product = Api POST '/catalog/api/products' @{ name = "smoke-$(Get-Date -Format HHmmss)"; price = 20; stock = 5 }
    if (-not $product.id) { throw 'no product id returned' }
}

Step 'Catalog: image upload (Blob Storage)' {
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
    $file = Join-Path ([IO.Path]::GetTempPath()) 'smoke.png'
    [IO.File]::WriteAllBytes($file, $png)
    $json = curl.exe -sS -f -H "Authorization: Bearer $token" -F "file=@$file;type=image/png" "$gw/catalog/api/products/$($product.id)/image"
    if ($LASTEXITCODE) { throw "upload failed (curl exit $LASTEXITCODE)" }
    $url = ($json | ConvertFrom-Json).imageUrl
    $downloaded = (Invoke-WebRequest $url -UseBasicParsing).Content
    if ($downloaded.Length -ne $png.Length) { throw "image at $url does not match" }
}

Step 'Basket: save basket (Redis)' {
    $basket = Api PUT '/basket/api/basket' @(@{ productId = $product.id; productName = $product.name; price = 20; quantity = 2 })
    if ($basket.total -ne 40) { throw "expected total 40, got $($basket.total)" }
}

Step 'Basket: checkout (Service Bus)' {
    $script:eventId = (Api POST '/basket/api/basket/checkout' @{ shippingAddress = 'Smoke Test St 1' }).eventId
    if (-not $eventId) { throw 'no event id returned' }
}

Step 'Order: order created from queue (KEDA scale-from-zero + PostgreSQL)' {
    $deadline = (Get-Date).AddSeconds($OrderTimeoutSeconds)
    do {
        $order = @(Api GET '/order/api/orders') | Where-Object id -eq $eventId
        if ($order) { if ($order.total -ne 40) { throw "expected total 40, got $($order.total)" }; return }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    throw "order $eventId not created within $OrderTimeoutSeconds s (check order-api logs and the queue's dead-letter count)"
}

Step 'Cleanup: delete test product' {
    if ($product.id) { Api DELETE "/catalog/api/products/$($product.id)" | Out-Null }
}

# ---------- Result ----------
if ($failures) { Write-Host "`n$failures check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
