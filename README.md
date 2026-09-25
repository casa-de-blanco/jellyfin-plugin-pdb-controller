# jellyfin-plugin-pdb-controller

Holds a Kubernetes **PodDisruptionBudget** open while Jellyfin is doing something a node
drain would interrupt: a selected scheduled task running, or a selected user streaming.

Jellyfin knows things Kubernetes does not — whether anyone is watching, and whether a
library scan is half done. This plugin is where that knowledge gets turned into a budget
the eviction API will respect.

## What it does

Every reconcile it asks the server what is happening and patches one field:

```
minAvailable: 1   while a selected task runs or a selected user is playing
minAvailable: 0   otherwise
```

Nothing else. The budget must already exist, and the plugin will never create, delete or
otherwise reshape it — in a GitOps cluster that object belongs to the repository, not to
this plugin.

## What it does not do

A PodDisruptionBudget gates the **Eviction API** only. It stops `kubectl drain` and
operator-driven node and OS upgrades. It does **not** stop:

- a rolling update, or `kubectl delete pod`
- a Deployment scaling to zero (KEDA, an HPA, a manual scale) — a scale-down deletes the
  pod directly and never consults a budget
- anything that bypasses eviction, such as `drain --disable-eviction`

If a scheduled task is being cut off by scale-to-zero rather than by a drain, this is the
wrong tool.

## Configuration

Dashboard → Plugins → PDB Controller.

- **Scheduled tasks** — a checkbox per task, plus *All scheduled tasks*. Tasks are stored
  by their stable key, so a rename or a localisation change does not drop the selection.
- **Streaming users** — a checkbox per user, plus *Any user*. A session counts only while
  it is actually playing something; an idle connected client does not. A *paused* stream
  does still count, because a drain should not kill a paused film either.
- **Grace period** — how long to wait after the last one ends before releasing, so a pause
  or a reconnect does not flap the budget.
- **Maximum hold** — a hold is broken after this long, with a warning. Without a cap, a
  wedged task or a session the server never reaped blocks drains and upgrades forever.
- **Reconcile interval** — the backstop. Events are acted on immediately; this is what
  recovers the state if one is ever missed.

Turning a master checkbox on does not erase the individual choices underneath it, so
turning it back off restores them. A task or user that appears later is covered
automatically under a master, and excluded until ticked without one.

## What it needs in the cluster

The pod must mount a service account token, and that account needs `get` and `patch` on
the one budget:

```yaml
rules:
  - apiGroups: ["policy"]
    resources: ["poddisruptionbudgets"]
    resourceNames: ["jellyfin"]
    verbs: ["get", "patch"]
```

`list` is deliberately absent — `resourceNames` cannot scope it, and the plugin only ever
reads the budget it is about to write.

Two things that are easy to miss:

- **The token has to actually be mounted.** Several Helm charts (bjw-s `app-template`
  among them) default the pod-level `automountServiceAccountToken` to `false`. The service
  account existing is not enough; with no token file the plugin logs once and stays idle.
- **Exclude `spec.minAvailable` from your GitOps controller.** The plugin owns that field
  at runtime. Under ArgoCD that means an `ignoreDifferences` entry *and*
  `RespectIgnoreDifferences=true` — `ignoreDifferences` alone only hides the diff, it does
  not stop the sync writing over a live hold.

## Operational notes

The budget is annotated while held, so the cluster can explain itself:

```console
$ kubectl get pdb jellyfin -o jsonpath='{.metadata.annotations}'
casa-de-blan.co/hold-since:  2026-09-24T21:14:07.0000000Z
casa-de-blan.co/hold-reason: streaming: zoe
```

If the server is SIGKILLed mid-hold the budget outlives it. That is the one state worth
knowing about, and it is why the first thing the plugin does on startup — before any
event can arrive — is reconcile: a stale hold is released within one interval of the
server coming back.

## Build

```console
dotnet build Jellyfin.Plugin.PdbController/Jellyfin.Plugin.PdbController.csproj -c Release
```

Targets `net10.0` against Jellyfin **12.1** (`targetAbi 12.1.0.0`). The plugin ships as a
single DLL with no runtime dependencies, on purpose: each plugin is loaded into its own
`AssemblyLoadContext` resolved from the plugin directory, and a transitive dependency that
disagrees with the server's own copy disables the plugin as `NotSupported` — a failure
that appears only on the target server, never in a local build. Two HTTP calls are not
worth that, so there is no Kubernetes client library here, just `HttpClient`.

Releases are cut by the `release` workflow, which builds, zips the DLL, publishes a GitHub
release and prepends the version to `manifest.json`.

## Installing

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/casa-de-blanco/jellyfin-plugin-pdb-controller/main/manifest.json
```

then install *PDB Controller* from the catalogue and restart.

Note the repository entry lives in the server's own `system.xml`, not in your Git
repository, so it does not survive a config volume rebuild.
