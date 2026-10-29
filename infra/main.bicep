targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment that can be used as part of naming resource convention, the name of the resource group for your application will use this name, prefixed with rg-')
param environmentName string

@minLength(1)
@description('The location used for all deployed resources')
param location string

@description('Id of the user or app to assign application roles')
param principalId string = ''

@metadata({azd: {
  type: 'generate'
  config: {length:32,noSpecial:true}
  }
})
@secure()
param health_probe_key string

var tags = {
  'azd-env-name': environmentName
}

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module api_identity 'api-identity/api-identity.module.bicep' = {
  name: 'api-identity'
  scope: rg
  params: {
    location: location
  }
}
module api_roles_messaging 'api-roles-messaging/api-roles-messaging.module.bicep' = {
  name: 'api-roles-messaging'
  scope: rg
  params: {
    location: location
    messaging_outputs_name: messaging.outputs.name
    principalId: api_identity.outputs.principalId
  }
}
module api_roles_sql 'api-roles-sql/api-roles-sql.module.bicep' = {
  name: 'api-roles-sql'
  scope: rg
  params: {
    location: location
    principalId: api_identity.outputs.principalId
    principalName: api_identity.outputs.principalName
    sql_outputs_name: sql.outputs.name
    sql_outputs_sqlserveradminname: sql.outputs.sqlServerAdminName
  }
}
module aspire_env 'aspire-env/aspire-env.module.bicep' = {
  name: 'aspire-env'
  scope: rg
  params: {
    aspire_env_acr_outputs_name: aspire_env_acr.outputs.name
    location: location
    userPrincipalId: principalId
  }
}
module aspire_env_acr 'aspire-env-acr/aspire-env-acr.module.bicep' = {
  name: 'aspire-env-acr'
  scope: rg
  params: {
    location: location
  }
}
module messaging 'messaging/messaging.module.bicep' = {
  name: 'messaging'
  scope: rg
  params: {
    location: location
  }
}
module migrator_identity 'migrator-identity/migrator-identity.module.bicep' = {
  name: 'migrator-identity'
  scope: rg
  params: {
    location: location
  }
}
module migrator_roles_sql 'migrator-roles-sql/migrator-roles-sql.module.bicep' = {
  name: 'migrator-roles-sql'
  scope: rg
  params: {
    location: location
    principalId: migrator_identity.outputs.principalId
    principalName: migrator_identity.outputs.principalName
    sql_outputs_name: sql.outputs.name
    sql_outputs_sqlserveradminname: sql.outputs.sqlServerAdminName
  }
}
module sql 'sql/sql.module.bicep' = {
  name: 'sql'
  scope: rg
  params: {
    location: location
  }
}
module worker_identity 'worker-identity/worker-identity.module.bicep' = {
  name: 'worker-identity'
  scope: rg
  params: {
    location: location
  }
}
module worker_roles_messaging 'worker-roles-messaging/worker-roles-messaging.module.bicep' = {
  name: 'worker-roles-messaging'
  scope: rg
  params: {
    location: location
    messaging_outputs_name: messaging.outputs.name
    principalId: worker_identity.outputs.principalId
  }
}
module worker_roles_sql 'worker-roles-sql/worker-roles-sql.module.bicep' = {
  name: 'worker-roles-sql'
  scope: rg
  params: {
    location: location
    principalId: worker_identity.outputs.principalId
    principalName: worker_identity.outputs.principalName
    sql_outputs_name: sql.outputs.name
    sql_outputs_sqlserveradminname: sql.outputs.sqlServerAdminName
  }
}
output API_IDENTITY_CLIENTID string = api_identity.outputs.clientId
output API_IDENTITY_ID string = api_identity.outputs.id
output API_IDENTITY_PRINCIPALNAME string = api_identity.outputs.principalName
output ASPIRE_ENV_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = aspire_env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
output ASPIRE_ENV_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = aspire_env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID
output ASPIRE_ENV_AZURE_CONTAINER_REGISTRY_ENDPOINT string = aspire_env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output ASPIRE_ENV_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = aspire_env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID
output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = aspire_env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = aspire_env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output MESSAGING_NAME string = messaging.outputs.name
output MESSAGING_SERVICEBUSENDPOINT string = messaging.outputs.serviceBusEndpoint
output MESSAGING_SERVICEBUSHOSTNAME string = messaging.outputs.serviceBusHostName
output MIGRATOR_IDENTITY_CLIENTID string = migrator_identity.outputs.clientId
output MIGRATOR_IDENTITY_ID string = migrator_identity.outputs.id
output SQL_SQLSERVERFQDN string = sql.outputs.sqlServerFqdn
output WORKER_IDENTITY_CLIENTID string = worker_identity.outputs.clientId
output WORKER_IDENTITY_ID string = worker_identity.outputs.id
output WORKER_IDENTITY_PRINCIPALNAME string = worker_identity.outputs.principalName
