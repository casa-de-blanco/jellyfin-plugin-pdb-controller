# jellyfin-pdb-controller (chart)

The cluster side of the [PDB Controller](../../README.md) Jellyfin plugin: the
PodDisruptionBudget it toggles, and the two RBAC objects that let it.

It does **not** deploy Jellyfin. Install it alongside whatever chart does, into
the same namespace.

```console
helm install jellyfin-pdb oci://ghcr.io/casa-de-blanco/charts/jellyfin-pdb-controller \
  --version 0.1.0 -n media
```

## What it renders

| Object | Name | Notes |
|---|---|---|
| `PodDisruptionBudget` | `{{ .Values.pdbName }}`, default `jellyfin` | Not release-prefixed — the plugin looks it up by the name on its settings page |
| `Role` | release fullname | `get` + `patch` on that one budget, nothing else |
| `RoleBinding` | release fullname | Subject is `serviceAccount.name`, in the release namespace |
| `ServiceAccount` | `serviceAccount.name` | Only with `serviceAccount.create=true` |

## `minAvailable` is deliberately not rendered

The plugin owns that field at runtime. Helm's three-way merge only reconciles
fields present in the old or the new manifest, so a chart that rendered
`minAvailable: 0` would stamp over a live hold on every `helm upgrade` — a drain
window opening mid-film, for the length of one reconcile interval.

A budget carrying neither `minAvailable` nor `maxUnavailable` computes
`desiredHealthy` 0 and allows disruptions. It fails open, exactly as an explicit
`0` would, and the plugin writes the real value on its first reconcile — which it
does at startup, before any event can arrive.

`pdb.minAvailable` exists if you want the resting state pinned by hand anyway.
Setting both it and `pdb.maxUnavailable` fails the render rather than the apply.

## Values

| Key | Default | |
|---|---|---|
| `pdbName` | `jellyfin` | Must match the plugin's `PdbName`. Feeds the budget's name *and* the Role's `resourceNames`, so the two cannot drift |
| `pdb.selector.matchLabels` | `app.kubernetes.io/name: jellyfin` | |
| `pdb.minAvailable` | `null` | Not rendered unless set — see above |
| `pdb.maxUnavailable` | `null` | Mutually exclusive with the above |
| `pdb.unhealthyPodEvictionPolicy` | `AlwaysAllow` | So a crashlooping Jellyfin cannot wedge a drain until it times out |
| `pdb.annotations` / `pdb.labels` | `{}` | |
| `rbac.create` | `true` | |
| `serviceAccount.create` | `false` | The Jellyfin chart normally owns the account |
| `serviceAccount.name` | `jellyfin` | The account the Jellyfin **pod** runs as |
| `commonLabels` / `commonAnnotations` | `{}` | |
| `nameOverride` / `fullnameOverride` | `""` | Role and RoleBinding names only |

## Two things the chart cannot do for you

**Mount the token.** Several charts — bjw-s `app-template` among them — default
the pod-level `automountServiceAccountToken` to `false`. The account existing is
not enough; with no token file the plugin logs once and stays idle.

**Keep your GitOps controller off `spec.minAvailable`.** Under ArgoCD that means
an `ignoreDifferences` entry **and** `RespectIgnoreDifferences=true`:

```yaml
ignoreDifferences:
  - group: policy
    kind: PodDisruptionBudget
    name: jellyfin
    jsonPointers:
      - /spec/minAvailable
syncPolicy:
  syncOptions:
    - RespectIgnoreDifferences=true
```

`ignoreDifferences` alone only hides the diff — it does not stop a sync writing
over a live hold, and the app reads `OutOfSync` for the length of every film.

## Releasing

The chart versions independently of the plugin. Bump `version` in `Chart.yaml`;
merging to `main` publishes it to `ghcr.io/casa-de-blanco/charts` via the `chart`
workflow. That workflow skips a version already in the registry rather than
overwriting it, so a merge that does not touch the version is a no-op.
