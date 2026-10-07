# MyEc infrastructure

## Deploy (one command)

Prereqs: [Azure CLI](https://aka.ms/installazurecliwindows). Docker is only needed if ACR cloud builds are blocked on your subscription.

```powershell
./infra/deploy.ps1 -AlertEmail you@example.com
```

Optional: `-Location swedencentral` (if a region is restricted on the free trial), `-EnvironmentName myec`, `-BudgetAmount 60`.

## GitHub Actions (CI/CD)

[.github/workflows/ci-cd.yml](../.github/workflows/ci-cd.yml): PRs build the solution and validate Bicep; pushes to `main` run `deploy.ps1`.

One-time setup (passwordless OIDC, run after `az login`):

```powershell
$sub = az account show --query id -o tsv
$app = az ad app create --display-name myec-github --query appId -o tsv
az ad sp create --id $app
# Owner: needed to create role assignments for the managed identities
az role assignment create --assignee $app --role Owner --scope /subscriptions/$sub
az ad app federated-credential create --id $app --parameters '{\"name\":\"main\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"repo:<owner>/<repo>:environment:production\",\"audiences\":[\"api://AzureADTokenExchange\"]}'
```

Then grant the `myec-github` app the Microsoft Graph **application** permission `Application.ReadWrite.OwnedBy` and **grant admin consent**. This lets it create the Entra ID app registration. Do this in the Entra portal: App registrations → myec-github → API permissions.

In GitHub → Settings, create an environment named `production` and add:
- Secrets: `AZURE_CLIENT_ID` (= `$app`), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
- Variables: `ALERT_EMAIL` (required), `AZURE_LOCATION`, `ENVIRONMENT_NAME` (optional)

## What gets created

| Resource | SKU | Est. cost / month |
|---|---|---|
| API Management | Consumption (1M calls free) | ~$0 |
| Container Apps ×3 (Catalog, Basket, Order) | Consumption, scale to zero | ~$0–5 |
| Container Registry | Basic | ~$5 |
| PostgreSQL Flexible Server | B1ms, 32 GB (free for 12 months on free accounts) | $0 (~$17 after) |
| Azure Managed Redis | Balanced_B0 (smallest) | ~$15–30 (largest item) |
| Service Bus | Basic (queue) | <$1 |
| Key Vault, Blob Storage | Standard | <$1 |
| App Insights + Log Analytics | 0.15 GB/day cap | ~$0 |
| Monitor alert + budget | | <$1 |

Roughly **$25–45/month**, well under the $200 credit. A budget alert emails you at 50%/80%/forecast 100%.

## Security model

- **No user service**: Microsoft Entra ID app registration (created by Bicep via the Graph extension) issues JWTs. Your user gets the `Catalog.Admin` role.
- **No secrets/keys**: each service has its own user-assigned managed identity. Postgres, Redis, Service Bus and Storage have key/password auth disabled.
- Key Vault holds the App Insights connection string, referenced by the container apps.

## Test

```powershell
$token = az account get-access-token --scope <entraScope output> --query accessToken -o tsv
curl -H "Authorization: Bearer $token" <apiGatewayUrl>/basket/api/basket
```

## Tear down

```powershell
az group delete -n rg-myec --yes
```
