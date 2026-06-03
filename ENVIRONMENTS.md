# Environment Setup — Dev / Test / Prod

## Branch → Environment Mapping

| Branch | Environment | Purpose |
|--------|-------------|---------|
| `dev`  | Development | Active development, auto-deploys on push |
| `test` | Testing     | QA/UAT, promote from dev via PR |
| `prod` | Production  | Live system, promote from test via PR |

## Workflow

```
dev  ──PR──>  test  ──PR──>  prod
```

1. **Dev**: All development happens here. Push/merge triggers auto-deploy.
2. **Test**: Create a PR from `dev` → `test`. Merge deploys to test environment.
3. **Prod**: Create a PR from `test` → `prod`. Merge deploys to production.

## Required GitHub Secrets

Each environment needs the following secrets configured in **GitHub → Settings → Secrets and variables → Actions**:

### DEV Environment
| Secret Name | Description |
|-------------|-------------|
| `AZURE_SWA_TOKEN_DEV` | Azure Static Web Apps deployment token (DEV) |
| `API_BASE_URL_DEV` | Functions API URL, e.g. `https://auctionsystem-dev.azurewebsites.net` |
| `AZURE_CLIENT_ID_DEV` | Azure AD app registration client ID for OIDC login |
| `AZURE_TENANT_ID_DEV` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID_DEV` | Azure subscription ID |
| `AZURE_FUNCTIONAPP_NAME_DEV` | Azure Function App name, e.g. `AuctionsystemDev` |
| `AZURE_RESOURCE_GROUP_DEV` | Azure resource group, e.g. `ITTEST` |

### TEST Environment
| Secret Name | Description |
|-------------|-------------|
| `AZURE_SWA_TOKEN_TEST` | Azure Static Web Apps deployment token (TEST) |
| `API_BASE_URL_TEST` | Functions API URL, e.g. `https://auctionsystem-test.azurewebsites.net` |
| `AZURE_CLIENT_ID_TEST` | Azure AD app registration client ID for OIDC login |
| `AZURE_TENANT_ID_TEST` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID_TEST` | Azure subscription ID |
| `AZURE_FUNCTIONAPP_NAME_TEST` | Azure Function App name, e.g. `AuctionsystemTest` |
| `AZURE_RESOURCE_GROUP_TEST` | Azure resource group |

### PROD Environment
| Secret Name | Description |
|-------------|-------------|
| `AZURE_SWA_TOKEN_PROD` | Azure Static Web Apps deployment token (PROD) |
| `API_BASE_URL_PROD` | Functions API URL, e.g. `https://auctionsystem-prod.azurewebsites.net` |
| `AZURE_CLIENT_ID_PROD` | Azure AD app registration client ID for OIDC login |
| `AZURE_TENANT_ID_PROD` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID_PROD` | Azure subscription ID |
| `AZURE_FUNCTIONAPP_NAME_PROD` | Azure Function App name, e.g. `AuctionsystemProd` |
| `AZURE_RESOURCE_GROUP_PROD` | Azure resource group |

## Azure Resources Per Environment

Each environment needs:

1. **Azure Function App** — hosts the API (AuctionSystem.Functions)
   - App Settings: `SqlConnectionString`, BC config, `AzureWebJobsStorage`, etc.
   - Each environment should point to its own database

2. **Azure Static Web App** — hosts the Blazor WASM frontend
   - The API URL is injected at build time via the `API_BASE_URL_*` secret

3. **Azure SQL Database** — separate database per environment
   - Dev: `testportaldb-dev`
   - Test: `testportaldb-test`
   - Prod: `testportaldb-prod`

4. **Azure Blob Storage** — for invoice/credit note PDFs (can share or separate per env)

## Migrating from base-init

The existing `base-init` branch has been renamed to `dev`. The old workflow files have been replaced with environment-specific ones:

- ~~`azure-static-web-apps-icy-beach-06b561303.yml`~~ → `deploy-dev.yml`
- ~~`base-init_auctionsystemazure.yml`~~ → `deploy-dev.yml` (combined)

### To complete the migration:

1. In GitHub → Settings → Branches → Default branch: change from `base-init` to `dev`
2. Add the DEV secrets listed above (you can reuse existing values):
   - `AZURE_SWA_TOKEN_DEV` = current `AZURE_STATIC_WEB_APPS_API_TOKEN_ICY_BEACH_06B561303`
   - `API_BASE_URL_DEV` = `https://auctionsystemazure-hjdgbugnfvfsaka7.westeurope-01.azurewebsites.net`
   - `AZURE_CLIENT_ID_DEV` = current `AZUREAPPSERVICE_CLIENTID_E8AA5F9DBDC340F1BE5460EFEB32D72C`
   - `AZURE_TENANT_ID_DEV` = current `AZUREAPPSERVICE_TENANTID_286EA316E3A844B58BDF711457511A4F`
   - `AZURE_SUBSCRIPTION_ID_DEV` = current `AZUREAPPSERVICE_SUBSCRIPTIONID_4DB58356E71E4A159489E77853B52456`
   - `AZURE_FUNCTIONAPP_NAME_DEV` = `Auctionsystemazure`
   - `AZURE_RESOURCE_GROUP_DEV` = `ITTEST`
3. Create the Azure resources for TEST and PROD environments
4. Add the TEST and PROD secrets
5. Create the `test` and `prod` branches from `dev`
