@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource migrator_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('migrator_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = migrator_identity.id

output clientId string = migrator_identity.properties.clientId

output principalId string = migrator_identity.properties.principalId

output principalName string = migrator_identity.name

output name string = migrator_identity.name