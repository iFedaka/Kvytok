@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aspire_env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('aspireenvacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'aspire-env-acr'
  }
}

output name string = aspire_env_acr.name

output loginServer string = aspire_env_acr.properties.loginServer

output id string = aspire_env_acr.id