@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

param aspire_env_outputs_azure_log_analytics_workspace_id string

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: take('insights-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    DisableLocalAuth: true
    WorkspaceResourceId: aspire_env_outputs_azure_log_analytics_workspace_id
  }
  tags: {
    'aspire-resource-name': 'insights'
  }
}

output appInsightsConnectionString string = insights.properties.ConnectionString

output name string = insights.name

output id string = insights.id