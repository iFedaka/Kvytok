@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aspire_env_outputs_azure_container_apps_environment_default_domain string

param aspire_env_outputs_azure_container_apps_environment_id string

param api_containerimage string

param api_identity_outputs_id string

param api_containerport string

param sql_outputs_sqlserverfqdn string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

param api_identity_outputs_clientid string

@secure()
param health_probe_key string

param aspire_env_outputs_azure_container_registry_endpoint string

param aspire_env_outputs_azure_container_registry_managed_identity_id string

resource api 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'healthchecks--probekey'
          value: health_probe_key
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(api_containerport)
        transport: 'http'
      }
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
          probes: [
            {
              httpGet: {
                httpHeaders: [
                  {
                    name: 'X-Health-Probe-Key'
                    value: health_probe_key
                  }
                ]
                path: '/alive'
                port: 8081
                scheme: 'HTTP'
              }
              type: 'Liveness'
            }
            {
              httpGet: {
                httpHeaders: [
                  {
                    name: 'X-Health-Probe-Key'
                    value: health_probe_key
                  }
                ]
                path: '/health'
                port: 8081
                scheme: 'HTTP'
              }
              type: 'Readiness'
            }
          ]
          image: api_containerimage
          name: 'api'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: '${api_containerport};8081'
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
              name: 'Reservation__HoldFor'
              value: '00:00:30'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: api_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
            {
              name: 'HealthChecks__Port'
              value: '8081'
            }
            {
              name: 'HealthChecks__ProbeKey'
              secretRef: 'healthchecks--probekey'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${api_identity_outputs_id}': { }
      '${aspire_env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}