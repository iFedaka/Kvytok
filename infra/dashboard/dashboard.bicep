// The one dashboard: reservations per minute, the reservation endpoint's latency, and errors.
// Hand-written, unlike the generated modules beside it in infra/: Aspire has no dashboard resource,
// so the AppHost adds this file as a Bicep template and azd infra gen copies it there.

@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@description('Resource id of the Application Insights component every tile queries.')
param insightsId string

// What each tile shows before anyone changes it: long enough to find a load test run in, short
// enough that one-minute bins stay readable
var timeRange = 'PT4H'

// A column of the query's result, as the chart reads it
type column = {
  name: string
  type: ('datetime' | 'long' | 'real' | 'string')
}

// A Log Analytics query tile scoped to the component. The inputs are the portal's own,
// as it exports them; every one must be present even when unset.
func logsPart(
  insightsId string,
  timeRange string,
  title string,
  subtitle string,
  query string,
  chart ('Line' | 'StackedColumn'),
  yAxis column[],
  splitBy column[]
) object => {
  type: 'Extension/Microsoft_OperationsManagementSuite_Workspace/PartType/LogsDashboardPart'
  inputs: [
    { name: 'resourceTypeMode', isOptional: true }
    { name: 'ComponentId', isOptional: true }
    { name: 'Scope', value: { resourceIds: [insightsId] }, isOptional: true }
    { name: 'PartId', value: guid(insightsId, title), isOptional: true }
    { name: 'Version', value: '2.0', isOptional: true }
    { name: 'TimeRange', value: timeRange, isOptional: true }
    { name: 'DashboardId', isOptional: true }
    { name: 'DraftRequestParameters', isOptional: true }
    { name: 'Query', value: query, isOptional: true }
    { name: 'ControlType', value: 'FrameControlChart', isOptional: true }
    { name: 'SpecificChart', value: chart, isOptional: true }
    { name: 'PartTitle', value: title, isOptional: true }
    { name: 'PartSubTitle', value: subtitle, isOptional: true }
    {
      name: 'Dimensions'
      value: {
        xAxis: { name: 'timestamp', type: 'datetime' }
        yAxis: yAxis
        splitBy: splitBy
        aggregation: 'Sum'
      }
      isOptional: true
    }
    { name: 'LegendOptions', value: { isEnabled: true, position: 'Bottom' }, isOptional: true }
    { name: 'IsQueryContainTimeRange', value: false, isOptional: true }
  ]
  settings: {}
}

// The Api exports the counter once a minute, so a minute's sum is that minute's count, across replicas
var reservationsQuery = '''
customMetrics
| where name == "kvytok.reservations"
| extend outcome = tostring(customDimensions["kvytok.reservation.outcome"])
| summarize reservations = sum(valueSum) by bin(timestamp, 1m), outcome
'''

var latencyQuery = '''
requests
| where cloud_RoleName == "api" and name == "POST /events/{eventId:guid}/reservations"
| summarize p50 = percentile(duration, 50), p95 = percentile(duration, 95), p99 = percentile(duration, 99) by bin(timestamp, 1m)
'''

// Application Insights marks every 4xx as failed, and a lost race for a seat is a 409: counting
// those would show contention as an outage. The Worker's spans carry no status code, only success.
var errorsQuery = '''
requests
| where success == false and not(resultCode startswith "4")
| summarize errors = count() by bin(timestamp, 1m), cloud_RoleName
'''

resource dashboard 'Microsoft.Portal/dashboards@2020-09-01-preview' = {
  name: take('dashboard-${uniqueString(resourceGroup().id)}', 64)
  location: location
  tags: {
    'hidden-title': 'Kvytok'
    'aspire-resource-name': 'dashboard'
  }
  properties: {
    lenses: [
      {
        order: 0
        parts: [
          {
            position: { x: 0, y: 0, colSpan: 12, rowSpan: 4 }
            metadata: any(logsPart(
              insightsId,
              timeRange,
              'Reservations per minute',
              'Seats held, and requests that found the seat already taken (409)',
              reservationsQuery,
              'StackedColumn',
              [{ name: 'reservations', type: 'real' }],
              [{ name: 'outcome', type: 'string' }]
            ))
          }
          {
            position: { x: 0, y: 4, colSpan: 6, rowSpan: 4 }
            metadata: any(logsPart(
              insightsId,
              timeRange,
              'Reserve seat latency (ms)',
              'POST /events/{eventId}/reservations, server side, per minute',
              latencyQuery,
              'Line',
              [
                { name: 'p50', type: 'real' }
                { name: 'p95', type: 'real' }
                { name: 'p99', type: 'real' }
              ],
              []
            ))
          }
          {
            position: { x: 6, y: 4, colSpan: 6, rowSpan: 4 }
            metadata: any(logsPart(
              insightsId,
              timeRange,
              'Errors per minute',
              'Failed requests and messages, not counting 4xx',
              errorsQuery,
              'StackedColumn',
              [{ name: 'errors', type: 'long' }],
              [{ name: 'cloud_RoleName', type: 'string' }]
            ))
          }
        ]
      }
    ]
    metadata: {
      model: {}
    }
  }
}

output name string = dashboard.name

output id string = dashboard.id
