# MyStravaStats

## Deploy to Azure from the command line

This application is an ASP.NET Core web app targeting .NET 10 and can be deployed to Azure App Service with the Azure CLI.

Relevant implementation details:

- The web app, shared sync library, and Azure Function target `net10.0`.
- Strava credentials are loaded from environment variables:
  - `STRAVA_CLIENT_ID`
  - `STRAVA_CLIENT_SECRET`
- Athlete stats export uses Azure Blob Storage and loads its connection string from:
  - `AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING`
- Auto sync stores encrypted Strava refresh tokens in the same storage account and loads its encryption key from:
  - `AUTO_SYNC_TOKEN_ENCRYPTION_KEY`
- The Azure Function timer schedule is loaded from:
  - `AutoSyncSchedule`
- The Strava OAuth callback endpoint is `/strava/callback`.
- Session state currently uses in-memory storage, so single-instance deployment is the safest starting point.

## Prerequisites

Install and authenticate with:

- .NET SDK
- Azure CLI
- An Azure subscription

Login:

```bash
az login
```

## Recommended Azure target

Use **Azure App Service** for the web app and **Azure Functions Flex Consumption** for the auto-sync timer.

## Deployment steps

Run these commands from the repository root.

### 1. Define deployment variables

```bash
export AZURE_LOCATION="westeurope"
export RESOURCE_GROUP="mystravastats-rg"
export PLAN="mystravastats-plan"
export APP_NAME="mystravastats$RANDOM"
export FUNCTION_APP_NAME="mystravastats-sync$RANDOM"
export FUNCTION_STORAGE_NAME="mystravasync$RANDOM"
export STRAVA_CLIENT_ID="your-client-id"
export STRAVA_CLIENT_SECRET="your-client-secret"
export AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING="your-storage-connection-string"
export AUTO_SYNC_TOKEN_ENCRYPTION_KEY="$(openssl rand -base64 32)"
```

### 2. Create Azure resources

```bash
az group create --name "$RESOURCE_GROUP" --location "$AZURE_LOCATION"
az appservice plan create --name "$PLAN" --resource-group "$RESOURCE_GROUP" --sku B1 --is-linux
az webapp create --resource-group "$RESOURCE_GROUP" --plan "$PLAN" --name "$APP_NAME" --runtime "DOTNETCORE|10.0"
az storage account create \
  --name "$FUNCTION_STORAGE_NAME" \
  --location "$AZURE_LOCATION" \
  --resource-group "$RESOURCE_GROUP" \
  --sku Standard_LRS \
  --allow-blob-public-access false
az functionapp create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --storage-account "$FUNCTION_STORAGE_NAME" \
  --flexconsumption-location "$AZURE_LOCATION" \
  --runtime dotnet-isolated \
  --runtime-version 10.0
```

If your chosen region does not support Flex Consumption, list available regions with:

```bash
az functionapp list-flexconsumption-locations --query "sort_by(@, &name)[].{Region:name}" -o table
```

### 3. Configure production app settings

```bash
az webapp config appsettings set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    STRAVA_CLIENT_ID="$STRAVA_CLIENT_ID" \
    STRAVA_CLIENT_SECRET="$STRAVA_CLIENT_SECRET" \
    AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING="$AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING" \
    AUTO_SYNC_TOKEN_ENCRYPTION_KEY="$AUTO_SYNC_TOKEN_ENCRYPTION_KEY"

az functionapp config appsettings set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --settings \
    AutoSyncSchedule="0 0 */4 * * *" \
    STRAVA_CLIENT_ID="$STRAVA_CLIENT_ID" \
    STRAVA_CLIENT_SECRET="$STRAVA_CLIENT_SECRET" \
    AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING="$AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING" \
    AUTO_SYNC_TOKEN_ENCRYPTION_KEY="$AUTO_SYNC_TOKEN_ENCRYPTION_KEY"
```

### 4. Publish the app locally

```bash
dotnet publish ./MyStravaStatsWebApp/MyStravaStatsWebApp.csproj -c Release -o ./artifacts/publish
(cd ./artifacts/publish && zip -r ../publish.zip .)
```

### 5. Deploy the published output

```bash
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --src-path ./artifacts/publish.zip \
  --type zip
```

### 6. Publish and deploy the Azure Function

The zip must contain the published Function output with `host.json` at the archive root. Do not zip the project folder itself.

```bash
dotnet publish ./MyStravaStats.AutoSyncFunction/MyStravaStats.AutoSyncFunction.csproj -c Release -o ./artifacts/function-publish
(cd ./artifacts/function-publish && zip -r ../function-publish.zip .)
az functionapp deployment source config-zip \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --src ./artifacts/function-publish.zip
```

You can also publish with Azure Functions Core Tools from the Function project folder:

```bash
(cd ./MyStravaStats.AutoSyncFunction && func azure functionapp publish "$FUNCTION_APP_NAME")
```

### 7. Check the Function deployment

```bash
az functionapp function show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --function-name StravaAutoSyncFunction

az functionapp log tail \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME"
```

### 8. Open the deployed app

```bash
az webapp browse --resource-group "$RESOURCE_GROUP" --name "$APP_NAME"
```

## Strava callback URL

After deployment, configure this callback URL in your Strava app:

```text
https://YOUR-APP-NAME.azurewebsites.net/strava/callback
```

Replace `YOUR-APP-NAME` with the actual Azure Web App name.

## Auto sync Azure Function

The timer Function lives in `MyStravaStats.AutoSyncFunction` and runs every 4 hours with:

```text
AutoSyncSchedule=0 0 */4 * * *
```

Configure the Function App with the same app settings as the web app plus normal Azure Functions settings:

```bash
az functionapp config appsettings set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --settings \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated \
    AutoSyncSchedule="0 0 */4 * * *" \
    STRAVA_CLIENT_ID="$STRAVA_CLIENT_ID" \
    STRAVA_CLIENT_SECRET="$STRAVA_CLIENT_SECRET" \
    AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING="$AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING" \
    AUTO_SYNC_TOKEN_ENCRYPTION_KEY="$AUTO_SYNC_TOKEN_ENCRYPTION_KEY"
```

The Function uses the isolated worker model on Azure Functions v4. For .NET 10 on Linux, use Flex Consumption, Premium, App Service, or another plan that supports the .NET 10 isolated stack rather than classic Linux Consumption.

The Function reads opted-in users from the `auto-sync-users` blob container, decrypts their saved Strava tokens, refreshes tokens when needed, and overwrites the existing `stats/{athleteId}.json` dashboard snapshot in the same format the Home page writes.

For local Function testing, copy `MyStravaStats.AutoSyncFunction/local.settings.sample.json` to `local.settings.json` and fill in real values. The real `local.settings.json` file is ignored by git.

## Important notes

### Secrets

Development secrets are currently present in local launch settings. Do not use those as production secrets. Store production values in Azure App Settings and rotate the Strava client secret if needed. Keep the same `AUTO_SYNC_TOKEN_ENCRYPTION_KEY` value configured in both the web app and Function App so the Function can decrypt records created by the web app.

### Session storage

The app currently uses in-memory session storage. Start with a single App Service instance. If you later scale out to multiple instances, move session state to a shared distributed cache such as Azure Cache for Redis.

### Runtime availability

If Azure App Service in your region does not yet expose `.NET 10` with:

```bash
az webapp create --resource-group "$RESOURCE_GROUP" --plan "$PLAN" --name "$APP_NAME" --runtime "DOTNETCORE|10.0"
```

then either:

- choose a runtime currently supported in your subscription/region, or
- deploy the app as a container instead.

## Optional shortcut

You can try `az webapp up` from the app directory for a shorter command-driven deployment, but the explicit publish-and-deploy flow above is more reliable for this project because it handles runtime selection, packaging, and environment variables clearly.
