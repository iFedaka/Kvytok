@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aspire_env_outputs_azure_container_apps_environment_default_domain string

param aspire_env_outputs_azure_container_apps_environment_id string

param migrator_containerimage string

param migrator_identity_outputs_id string

param sql_outputs_sqlserverfqdn string

param migrator_identity_outputs_clientid string

param aspire_env_outputs_azure_container_registry_endpoint string

param aspire_env_outputs_azure_container_registry_managed_identity_id string

resource migrator 'Microsoft.App/jobs@2025-07-01' = {
  name: 'migrator'
  location: location
  properties: {
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      registries: [
        {
          server: aspire_env_outputs_azure_container_registry_endpoint
          identity: aspire_env_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: aspire_env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: migrator_containerimage
          name: 'migrator'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ConnectionStrings__kvytok'
              value: 'Server=tcp:${sql_outputs_sqlserverfqdn},1433;Encrypt=True;Authentication="Active Directory Default";Database=kvytok'
            }
            {
              name: 'KVYTOK_HOST'
              value: sql_outputs_sqlserverfqdn
            }
            {
              name: 'KVYTOK_PORT'
              value: '1433'
            }
            {
              name: 'KVYTOK_URI'
              value: 'mssql://${sql_outputs_sqlserverfqdn}:1433/kvytok'
            }
            {
              name: 'KVYTOK_JDBCCONNECTIONSTRING'
              value: 'jdbc:sqlserver://${sql_outputs_sqlserverfqdn}:1433;database=kvytok;encrypt=true;trustServerCertificate=false'
            }
            {
              name: 'KVYTOK_DATABASENAME'
              value: 'kvytok'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: migrator_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
          ]
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migrator_identity_outputs_id}': { }
      '${aspire_env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}