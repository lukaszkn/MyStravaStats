# MyStravaStats

## Deploy to Azure from the command line

This application is an ASP.NET Core web app targeting .NET 10 and can be deployed to Azure App Service with the Azure CLI.

Relevant implementation details:

- The app target framework is `net10.0`.
- Strava credentials are loaded from environment variables:
  - `STRAVA_CLIENT_ID`
  - `STRAVA_CLIENT_SECRET`
- Athlete stats export uses Azure Blob Storage and loads its connection string from:
  - `AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING`
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

Use **Azure App Service** for this project.

## Deployment steps

Run these commands from the repository root.

### 1. Define deployment variables

```bash
export AZURE_LOCATION="westeurope"
export RESOURCE_GROUP="mystravastats-rg"
export PLAN="mystravastats-plan"
export APP_NAME="mystravastats$RANDOM"
export STRAVA_CLIENT_ID="your-client-id"
export STRAVA_CLIENT_SECRET="your-client-secret"
export AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING="your-storage-connection-string"
```

### 2. Create Azure resources

```bash
az group create --name "$RESOURCE_GROUP" --location "$AZURE_LOCATION"
az appservice plan create --name "$PLAN" --resource-group "$RESOURCE_GROUP" --sku B1 --is-linux
az webapp create --resource-group "$RESOURCE_GROUP" --plan "$PLAN" --name "$APP_NAME" --runtime "DOTNETCORE|10.0"
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
    AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING="$AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING"
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

### 6. Open the deployed app

```bash
az webapp browse --resource-group "$RESOURCE_GROUP" --name "$APP_NAME"
```

## Strava callback URL

After deployment, configure this callback URL in your Strava app:

```text
https://YOUR-APP-NAME.azurewebsites.net/strava/callback
```

Replace `YOUR-APP-NAME` with the actual Azure Web App name.

## Important notes

### Secrets

Development secrets are currently present in local launch settings. Do not use those as production secrets. Store production values in Azure App Settings and rotate the Strava client secret if needed.

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
