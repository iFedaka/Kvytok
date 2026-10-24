@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aspire_env_outputs_azure_container_apps_environment_default_domain string

param aspire_env_outputs_azure_container_apps_environment_id string

param worker_containerimage string

param worker_identity_outputs_id string

param sql_outputs_sqlserverfqdn string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

param worker_identity_outputs_clientid string

param aspire_env_outputs_azure_container_registry_endpoint string

param aspire_env_outputs_azure_container_registry_managed_identity_id string

resource worker 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'worker'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: aspire_env_outputs_azure_container_registry_endpoint
          identity: aspire_env_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: aspire_env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: worker_containerimage
          name: 'worker'
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
              name: 'ConnectionStrings__reservations'
              value: 'Endpoint=${messaging_outputs_servicebusendpoint};EntityPath=reservations'
            }
            {
              name: 'RESERVATIONS_HOST'
              value: messaging_outputs_servicebushostname
            }
            {
              name: 'RESERVATIONS_URI'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'RESERVATIONS_QUEUENAME'
              value: 'reservations'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: worker_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${worker_identity_outputs_id}': { }
      '${aspire_env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}