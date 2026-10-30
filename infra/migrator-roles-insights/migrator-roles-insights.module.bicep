@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param insights_outputs_name string

param principalId string

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insights_outputs_name
}

resource insights_MonitoringMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalType: 'ServicePrincipal'
  }
  scope: insights
}