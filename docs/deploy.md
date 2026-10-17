# Deploying Kvytok

A runbook, not a tutorial. It assumes the deploy works, and tells you what to do
when it doesn't, what can no longer be changed, and how to tell whether a deploy
actually succeeded rather than merely finished.

## What gets deployed

Two Container Apps (`api`, `worker`), one Container Apps job (`migrator`), one
Azure SQL database, a Container Registry, a Container Apps environment and a Log
Analytics workspace, all into the resource group `rg-kvytok-dev` in
**Sweden Central**.

```powershell
azd provision     # shared infrastructure: resource group, registry, environment, SQL
azd deploy        # the Container Apps themselves, built from the Aspire manifest
```

`azd up` runs both, but run them separately — see "Known failures". Note the
split: the Container Apps are created by **deploy**, not by provision. Changing
ingress or replica counts in the AppHost takes effect on a deploy, and
`azd provision` will correctly report "no changes".

The infrastructure is committed. `infra/` holds the Bicep for provision, and
`src/Kvytok.AppHost/infra/` holds the per-app templates that deploy fills in.
azd generated both from the AppHost (`azd infra gen`) and now reads the files
instead of the AppHost. A change to the AppHost's Azure shape (scale, ingress,
probes, a new resource) does nothing until you regenerate and commit the files.

Deploying from a clean machine also needs the environment to exist first:

```powershell
azd env new kvytok-dev     # prompts for subscription and location
```

**The public URL is not stable across teardowns.** The environment's default
domain is minted when the Container Apps environment is created, so every
`azd down` followed by `azd up` produces a different hostname. Anywhere the API
address is published (e.g. README) has to be updated after a rebuild.
If that becomes a problem, the fix is a custom domain, not a convention.

## Decisions that cannot be undone

**The region is Sweden Central, permanently.** The Azure SQL free offer binds to
one region per subscription and does not move. Relocating compute would leave the
free database behind and start a real bill.

**Not every region is available.** This subscription is refused SQL provisioning
in North Europe (`ProvisioningDisabled`), and was earlier refused App Service in
West Europe (`RequestDisallowedByAzure`). These are subscription-level
restrictions and are not visible in advance. To test a region before committing
to it, create a throwaway logical server — it costs nothing and fails in about
eight seconds if the region is closed:

```powershell
az sql server create -g rg-kvytok-dev -n kvytok-probe-REGION -l REGION `
  -u probeadmin -p "Probe-$((New-Guid).Guid)"
az sql server delete -g rg-kvytok-dev -n kvytok-probe-REGION --yes
```

**The region lives in the azd environment, not in the command.** Re-running
`azd up` does not ask again; it silently reuses the stored region. To change it,
delete `.azure/<env>/` and recreate the environment under the same name.

## The probe port

Health endpoints are not served on the public ingress. They are bound to port
**8081**, which is not routed externally, and gated by a key in a request header.
The branch is terminal: that port serves exactly two paths and nothing else.

Three behaviours, all worth re-checking after any change to the pipeline:

| Request on 8081 | Expected |
| --- | --- |
| `/alive` without the key | `401` with a `WWW-Authenticate` header |
| `/alive` with the key | `200 Healthy` |
| `/` with the key | `404` — the port is not the application |

On the public address, `/health` and `/alive` return 404. That is the control
working, not a fault.

The platform uses the same port. The API has a liveness probe on `/alive` and a
readiness probe on `/health`, both sending the key (`HealthProbes.cs` in the
AppHost). Probe headers cannot reference secrets, so the key also appears in
plain text in the app's probe configuration. The worker has no ingress and no
probes.

The key is a Container App secret. To read it:

```powershell
az containerapp secret list -g rg-kvytok-dev -n api -o table
```

Never paste its value into a commit, an issue, or a chat.

## Verifying a deploy

A green `azd deploy` does not mean a correct deploy. Check all five.

**Shape of the apps.**

```powershell
az containerapp list -g rg-kvytok-dev `
  --query "[].{name:name, external:properties.configuration.ingress.external, min:properties.template.scale.minReplicas, max:properties.template.scale.maxReplicas}" -o table
```

Expected: `api` external `True` with 0–3 replicas, `worker` with no ingress at
all and 0–1. Container Apps default to min 1 / max 10 and no external ingress, so
both must be set explicitly in the AppHost. Getting this wrong gives you either a
round-the-clock bill or an API nobody outside can reach.

**Probes are configured.**

```powershell
az containerapp show -g rg-kvytok-dev -n api --query "properties.template.containers[0].probes[].{type:type, path:httpGet.path, port:httpGet.port}" -o table
```

Expected: `Liveness` on `/alive` and `Readiness` on `/health`, both on 8081.

**The public surface.** The root document returns JSON, `/scalar` renders the API
reference, `/openapi/v1.json` returns the document.

**The probe port**, through the three cases in the table above. There is no
`curl` or `wget` in the runtime image — that is deliberate, a minimal image has
less to patch. Use bash's built-in TCP instead; `/bin/sh` is dash and does not
have it:

```powershell
az containerapp exec -g rg-kvytok-dev -n api --command "/bin/bash"
```

```sh
exec 3<>/dev/tcp/localhost/8081 && \
  printf 'GET /alive HTTP/1.1\r\nHost: localhost\r\nX-Health-Probe-Key: KEY\r\nConnection: close\r\n\r\n' >&3 && \
  cat <&3
```

**Logs.** Both apps should be writing to Log Analytics.

## When the app looks dead

Both apps scale to zero. The first request after an idle period wakes a container
and can take several seconds or time out outright. Retry before investigating.

The same applies to logs: at zero replicas there is nothing producing them, so
wake the app with a request before reading.

```powershell
az containerapp logs show -g rg-kvytok-dev -n api --tail 200
```

A useful detail on startup: ASP.NET Core logs `Now listening on:` once per bound
address. If port 8081 is missing from that list, the probe port did not bind and
no amount of header debugging will help.

## Known failures and what they actually mean

**`Failed: Azure SQL Server` in under ten seconds.** Not capacity and not a
timeout — a validation or policy refusal. azd's summary line ("a resource with
this name already exists or is in a conflicting state") is usually noise; the
real message is in the deployment:

```powershell
az deployment group list -g rg-kvytok-dev --query "[?properties.provisioningState=='Failed'].name" -o tsv
az deployment operation group list -g rg-kvytok-dev -n NAME `
  --query "[?properties.provisioningState=='Failed'].properties.statusMessage" -o json
```

**`empty dotnet configuration output`.** The suggestion to set
`EnableSdkContainerSupport` is misleading — the worker builds a valid container
image locally without it. This appeared once during `azd up`, where packaging
overlaps with provisioning, and did not reappear once provision and deploy ran as
separate steps. Treat it as transient; this is why the two commands are split at
the top of this document.

**`DotNetComponentOperationError: aspire-dashboard`.** The managed Aspire
dashboard failing to provision. It is not deployed here, and the dashboard URL
azd prints is a reserved address that serves nothing. Harmless.

**A resource group that will not recreate.** Soft-deleted resources hold their
names. See "Teardown" — `--purge` does less than its name suggests.

**A 404 where you expected an endpoint.** Two causes seen so far, both worth
checking before suspecting Azure. Endpoints mapped inside
`if (app.Environment.IsDevelopment())` do not exist in a container, where the
environment is Production. And a `UseWhen` branch whose predicate includes an
authorization check falls through to the main pipeline when the check fails,
producing a 404 instead of a 401 — put the branch on the port and the check
inside it.

## Cost

The budget `Budget20` is scoped to the subscription, $20 a month, alerting at
50 / 80 / 100 % by email. **A budget does not stop anything.** It is a
notification, not a cap.

The Container Registry (Basic) costs a flat few dollars a month regardless of
use. Container Apps compute is free up to a monthly grant and billed per second
after it; both apps sit at min 0 replicas, so idle time is close to free. The API
wakes on an HTTP request. The worker currently never wakes at all, because it has
no scale rule yet.

The database is on the free offer (`useFreeLimit: true`, `GP_S_Gen5`) with
`freeLimitExhaustionBehavior: AutoPause` — when the monthly allowance is spent
the database pauses rather than billing. Anything that touches the database on
every request spends that allowance, which is the real reason the health endpoint
is not public: `/health` runs a database check, `/alive` does not.

The readiness probe does call `/health`, so a running API replica queries the
database on every probe interval. That cost is accepted: probes run only while a
replica is up, and with scale-to-zero that is a small share of the month. A
readiness check that confirms the database is reachable is worth more than the
allowance it spends. If the allowance starts running out early, drop the database
check from the readiness probe before anything else.

## Teardown

```powershell
azd down --purge --force
```

Expect 18–28 minutes; most of it is the Log Analytics workspace. Measured: 25m30s
without `--purge`, 18m15s with it. Do not interrupt it — a half-deleted resource
group is the worst state to be in.

**`--purge` does not purge Log Analytics, despite reporting that it does.** One
run printed "Done: Purging Log Analytics Workspace" and the workspace was still
soft-deleted afterwards. Verify rather than trust:

```powershell
az monitor log-analytics workspace list-deleted-workspaces -o table
```

Soft-deleted workspaces hold their names for 30 days. This does not block a
redeploy — creating a workspace with the same name, resource group and region
*recovers* the old one — but that means a later deployment silently adopts a
previous environment's logs. To remove one for good you have to recover it first,
and recovery needs the resource group, so the group has to come back briefly:

```powershell
az group create -n rg-kvytok-dev -l swedencentral
$dead = az monitor log-analytics workspace list-deleted-workspaces `
  --query "[?resourceGroup=='rg-kvytok-dev'].name" -o tsv

foreach ($w in $dead) {
  az monitor log-analytics workspace recover -g rg-kvytok-dev -n $w
  az monitor log-analytics workspace delete -g rg-kvytok-dev -n $w --force true --yes
}
az monitor log-analytics workspace list-deleted-workspaces -o table   # expect empty
az group delete -n rg-kvytok-dev --yes
az group exists -n rg-kvytok-dev                                      # must be false
```

The group's own location need not match the workspaces; each restores to its
original region. Wait for `az group exists` to return `false` before the next
`azd up`, which wants the same group name.

When abandoning an environment entirely rather than tidying it,
`az group delete --no-wait` is far faster than `azd down`.

## Deliberately not done yet

Listed so a reader does not mistake these for oversights.

**Migrations run after the new code, not before it.** They live in their own
project, `Kvytok.MigrationService`, deployed as the `migrator` Container Apps job
and started by the azd `postdeploy` hook (`scripts/run-migrations.ps1`), which
fails the deploy if the job does not succeed. Because the hook runs after azd has
deployed every service, new API and worker revisions are briefly live against the
old schema. Fine while every migration is additive; a breaking one needs the
migrator to run ahead of the apps, or an expand-then-contract migration.

**The worker cannot wake itself.** Minimum replicas is 0 with no scale rule,
because there is no queue yet. When Service Bus arrives it needs a KEDA scale
rule on queue length, or it will sleep through every message.

**No CI deployment.** Deploys are run by hand from a workstation.
