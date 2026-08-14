# Day 5 — Azure Container Apps environment

Subscription: Azure subscription 1
Region: Central India

## Commands

Register the required resource providers (Container Apps will not create
without these):

    az provider register --namespace Microsoft.App
    az provider register --namespace Microsoft.OperationalInsights

Create the resource group:

    az group create -n thinkschool-rg -l centralindia

Create the Container Apps environment (took roughly 3 minutes):

    az containerapp env create -n thinkschool-env -g thinkschool-rg -l centralindia

Show it:

    az containerapp env show -n thinkschool-env -g thinkschool-rg

## Output of az containerapp env show (abridged — full JSON in submission)

    "name": "thinkschool-env",
    "location": "Central India",
    "resourceGroup": "thinkschool-rg",
    "properties": {
      "provisioningState": "Succeeded",
      "defaultDomain": "orangebush-c5a6d4ef.centralindia.azurecontainerapps.io",
      "staticIp": "20.204.234.66",
      "appLogsConfiguration": {
        "destination": "log-analytics",
        "logAnalyticsConfiguration": {
          "customerId": "14286fbc-ed93-432f-b869-aeaecbb9ca82"
        }
      },
      "kedaConfiguration": { "version": "2.18.1" },
      "daprConfiguration": { "version": "1.16.4-msft.11" },
      "workloadProfiles": [
        { "name": "Consumption", "workloadProfileType": "Consumption" }
      ],
      "publicNetworkAccess": "Enabled",
      "zoneRedundant": false
    }

## What the environment gives you

The environment is the shared boundary the card describes, and the JSON shows
each part concretely:

- defaultDomain — apps deployed here get a subdomain automatically with TLS.
  That is the built-in ingress; no load balancer to configure.
- appLogsConfiguration — a Log Analytics workspace was auto-created
  (workspace-thinkschoolrgA8Cj). Every app in this environment logs to the same
  workspace, which is what "shared logging" means.
- kedaConfiguration — KEDA is the autoscaler, so scale rules are declarative
  (--scale-rule) rather than something the app implements.
- workloadProfiles: Consumption — scale to zero, pay per use.
- staticIp — one outbound IP shared by every app in the environment, which
  matters for firewall allow-lists on downstream services.

## Deploying an app (not part of this exercise)

The flags the card highlights would be used like this:

    az containerapp create -n quotes-api -g thinkschool-rg \
      --environment thinkschool-env \
      --image <registry>/quotes-api:0.1.0 \
      --ingress external --target-port 8080 \
      --scale-rule-name http-rule --scale-rule-type http \
      --scale-rule-http-concurrency 50

--target-port must be 8080 to match the port the container image from
Day 5's dotnet publish task listens on.
