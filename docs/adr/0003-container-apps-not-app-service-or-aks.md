---
status: accepted
date: 2026-11-16
---

# Run on Container Apps, not App Service or AKS

## Context and Problem Statement

Kvytok is two long-running processes and one run-to-completion job: the Api (HTTP, up to three
replicas), the Worker (no ingress, driven by the `reservations` queue) and the migrator that
runs at deploy. Each is a container built from the Aspire manifest. Which Azure service hosts
them?

## Decision Drivers

* Everything scales to zero. An idle month should cost close to nothing, and the monthly budget
  is $20 ([deploy.md](../deploy.md#cost)).
* The Worker must be woken by the length of a Service Bus queue, not by HTTP traffic.
* No cluster, node or plan to patch, size or pay for while nobody is using the system.
* One person runs it. Operating effort counts as a cost.
* The platform must be available to this subscription, which has been refused services by region
  before ([deploy.md](../deploy.md#decisions-that-cannot-be-undone)).

## Considered Options

* Azure Container Apps
* Azure App Service
* Azure Kubernetes Service (AKS)

## Decision Outcome

Chosen option: "Azure Container Apps", because it is the only one of the three where both
requirements, scale to zero and a worker woken by queue length, are properties of the platform
and not something built on top of it. KEDA is part of the service: the Worker's scale rule is a
few lines of configuration on the app (`azure-servicebus`, polled every 10 seconds, authenticated
with the app's own managed identity), and the migrator is a Container Apps job in the same
environment.

### What it costs

* **A scale rule is load-bearing, and the platform does not say so.** A Container App with no
  HTTP ingress and no scale rule did not scale to zero, whatever `minReplicas` said. The Worker
  ran round the clock for about 49 replica-hours, and the October compute line of $2.85 is that
  stretch ([deploy.md](../deploy.md#what-the-october-invoice-shows)). The rule was written
  before it was deployed, and it did nothing until it was.
* **The Container Apps are created by `azd deploy`, not by `azd provision`.** A change to their
  shape, scale included, is not applied by provisioning, which is how that gap opened.
* **Defaults favour an always-on, unreachable app.** Container Apps default to a minimum of 1
  and a maximum of 10 replicas and no external ingress, so both are set explicitly in the
  AppHost.
* **A cold start is in the path.** The first request after an idle period waits for a container
  to start, and the first message for a cold Worker waits for a poll and a container start. The
  design works around this rather than removing it (see "Reservation created" in
  [decisions.md](../decisions.md)).
* **Less control than Kubernetes.** There is no access to the node, the scheduler or the
  Kubernetes API. Nothing here needs them, which is the reason this is acceptable and not a
  strength.

### Consequences

* Good, because there is no cluster, node pool or App Service plan to run, patch or size.
* Good, because idle compute is free. In the first 13 days of November, including a load test of
  about 1,430 reservation requests, Container Apps produced no charge at all.
* Good, because the whole deployed shape is a handful of Bicep modules generated from the
  AppHost.
* Bad, because the cost behaviour depends on scale rules the platform will not warn about
  leaving out. The check is a query in the runbook, not a guard in the platform.
* Bad, because the system is tied to a managed service. Moving to Kubernetes later means
  rewriting the deployment, though not the containers.

### Confirmation

* The runbook's "shape of the apps" query shows the Api external at 0–3 replicas and the Worker
  with no ingress at 0–1 and an `azure-servicebus` rule on `reservations`
  ([deploy.md](../deploy.md#verifying-a-deploy)).
* The Worker's replica count is about zero outside load: the replica-hours stop on 26 October
  when the revision carrying the rule was deployed, and stay there.
* [load-test.md](../load-test.md) records the Api scaling out under load, to at most three
  replicas, on the platform's default HTTP rule. The bursts did not start from zero: the cold
  start from zero took over a minute and was waited out before measuring.

## Why not the alternatives

### Azure App Service

* Good, because it is the simplest home for a web API, and deployment is well understood.
* Bad, because an App Service plan is billed while it exists, and a plan does not scale to zero.
  The always-on cost this system is built to avoid would be the baseline.
* Bad, because it has no queue-length trigger to wake a worker. Its autoscale works from metrics,
  and scaling to zero is not among them. Running the Worker as a WebJob or a separate plan would
  put the idle cost back, and polling the queue from the Api would undo the separation.
* Bad, because this subscription was refused App Service in West Europe
  (`RequestDisallowedByAzure`). That is a restriction of the subscription and not of App Service,
  but it was a fact when the choice was made, and it is invisible until 
  a deploy fails.

### Azure Kubernetes Service (AKS)

* Good, because it can do everything Container Apps does here and more: KEDA is available as an
  add-on, so the same queue-driven scale rule would work.
* Bad, because pods can scale to zero but nodes cannot. A cluster needs a system node pool that
  is always on, so the cost this system is built to avoid is back, however small the node.
* Bad, because a cluster is something to operate: Kubernetes upgrades, node images, ingress,
  workload identity wiring, and certificates. For three small containers that is more work than
  the system.
* Bad, because the control plane being free does not make the cluster free. The nodes are the
  bill.

## When this would be the wrong call

This decision is about this system, and AKS would have been the better answer for others. It becomes the
right choice, and the cost above is worth paying, when any of these hold:

* **There is more than a handful of services.** The fixed cost of a cluster is shared across
  workloads. At three containers it is the whole bill; at thirty it is a rounding error. The
  point where that turns is not known here and depends on the workloads.
* **Load is steady, not bursty.** Scale to zero is Container Apps' main advantage, and it is worth
  nothing for a service that is never idle. With constant traffic, always-on nodes at high
  utilisation can be cheaper per unit of work than a platform that charges for the convenience.
* **The workload needs something Container Apps will not give.** Daemon sets, privileged or
  host-level access, a service mesh, custom operators or CRDs, GPU or specialised node types,
  fine control over scheduling, networking or storage. This system needs none of them.
* **Kubernetes is already how the organisation deploys.** If a team already runs clusters, with
  their tooling, policy and on-call, a new workload on the existing cluster costs much less than a
  second platform to learn. For one person with no cluster, the opposite is true.
* **Portability is a requirement and not a hope.** Helm charts and the Kubernetes API move
  between clouds and on-premises in a way Container Apps does not.
* **The platform's limits are reached.** Replica counts, resource sizes or networking features
  that Container Apps does not offer would force the move.

None of these is true of Kvytok today. If the first is, the cost side of this decision should be
looked at again; if one of the third or fifth is, it is no longer a question of cost.
