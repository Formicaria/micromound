# Micromound.Capabilities

The capability kernel. This is the physical authority boundary: the one place a request for
physical work becomes permission to perform it.

## Why it is a project and not a class

Every other layer can be wrong without anything moving. This one cannot. Isolating it means the
authority rules have no dependency on the runtime, the drivers, the transport, or any reasoning
provider — it depends on `Micromound.Protocol` and nothing else, and the dependency arrow never
points back. A reasoning provider cannot reach the kernel's internals because it cannot reference
this project without a cycle.

## The seam

Workers ask for semantic operations:

```csharp
kernel.Execute(new CapabilityRequest
{
    Capability = "act.water_valve",
    Parameters = { ["on_s"] = 10 },
    Worker = "Watering Ant",
    WorkerCeiling = ActionClass.Benign
}, now);
```

They never ask for `GPIO17 = HIGH`, and there is no field through which they could. Drivers are
reached only through `ICapabilityExecutor`, executors are held only by `CapabilityKernel`, and
nothing hands one out.

## Two-stage on purpose

`Authorize` is pure: registries + authority + history + request + `now` in, a `KernelDecision`
out. Nothing is recorded, nothing moves, and calling it twice answers twice the same. `Execute`
authorizes, performs, records duty-cycle history, and runs the evidence gate.

The split is what makes the authority rules testable with no hardware, no simulator, and no wall
clock — which is why the tests for this project are the fastest ones in the repo.

## Check order

The order is a safety statement, not an implementation detail:

| # | Check | Refusal |
|---|---|---|
| 1 | Stop order in force | `stopped` |
| 2 | Id registered in this build | `unknown_capability` / `routine_not_registered` |
| 3 | Driver reports itself healthy | `capability_unavailable` |
| 4 | Hazardous class | `hazardous_prohibited` |
| 5 | Class within the ceiling the charter and lease leave in force | `no_charter` / `lease_expired` / `action_class_exceeded` |
| 6 | Granted by this specific charter | `not_granted` / `routine_not_enabled` |
| 7 | Within the requesting worker's own ceiling | `action_class_exceeded` |
| 8 | Parameter names accepted, required names present | `unknown_parameter` / `missing_parameter` |
| 9 | hardware ∩ device ∩ charter | — |
| 10 | Minimum off-time elapsed | `duty_cycle` |
| 11 | Trailing-hour rate | `rate_limit` |
| 12 | Clamp rather than refuse, and say what narrowed | — |
| 13 | An executor is bound | `executor_missing` |

Stop is first because it must work when everything else is broken. Hazardous is refused before
authority is even consulted, so that no charter can ever be the reason it was allowed.

## Three limit tiers

```
hardware/firmware   ∩   device manifest   ∩   charter   =   effective
   (innermost)          (operator config)     (grant)
```

Ceilings take the minimum, floors take the maximum. An outer tier can only narrow. A charter
asking for a shorter off-time than the hardware demands does not get one — it is intersected
away silently at execution and reported loudly at validation.

## What is deliberately not here

- **Workflow.** Sequencing steps is `Micromound.Runtime`'s job. The kernel answers one request.
- **Evidence storage.** The kernel runs `EvidenceGate` and reports the verdict; capture,
  correlation, and the local store are `Micromound.Evidence`.
- **Transport.** The kernel never learns whether the mound is online. `KernelAuthority` knows only
  that a lease has or has not expired, which is the same question offline and connected.
- **Any notion of "urgency".** There is no argument, field, or flag through which a caller can
  ask for an exception. Ambiguity resolves downward, and the way to guarantee that is to give
  ambiguity nowhere to enter.
