# Micromound.Runtime

The local colony runtime — the Mound Major and the six default ants.

## What lives here

`IMoundMajor` coordinates: it accepts charters and manifests, walks a mission's ordered steps,
evaluates deterministic conditions, dispatches to workers, and produces a `MissionReport`. It is a
workflow and state-machine coordinator, **not** an always-running agent — most missions never
touch a model at all.

The six ants (`Ants.cs`) are logical workers, not processes and not model instances: Scout
(sensing), Forager (requesting action), Guard (health and safety observation), Witness (outcome
confirmation), Cache (short-term state), Runner (transport). Application-specific ants — Soil,
Drive, Vision Inspection — are declared in the manifest and layered on top without changing this
project.

## What deliberately does not live here

Actuation. `IForagerAnt` submits a `CapabilityRequest` and receives an `ActionRecord`; it holds no
executor and no driver handle. The runtime references `Micromound.Capabilities` to *call* the
kernel, never to bypass it.

Status: M2 complete at `v0.6.0`. All six default ants are runtime services — Scout, Forager and
Guard on the mission path (v0.4.0), Witness on the verdict (v0.5.0), Cache and Runner on the
record (v0.6.0).
