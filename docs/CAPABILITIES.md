# Capabilities and routines

A **capability** is a machine-readable primitive describing what physical functionality exists.
Capabilities are the vocabulary in which every physical request is expressed, and the reason the
system scales across hardware without the runtime — or the upstream controller — knowing anything
about boards, buses, or part numbers.

## Naming

```text
<namespace>.<segment>[.<segment>…]
```

Three namespaces, closed:

| Prefix | Meaning | Action class |
|---|---|---|
| `sense.` | Observes the world | Always `observe`; the registry enforces it |
| `act.` | Actuates | Declared by the driver |
| `routine.` | Invokes a registered deterministic sequence | Declared by the routine |

Every segment is lowercase `[a-z0-9_]`. This is restrictive on purpose: these strings appear in
charters, evidence patterns, and firmware tables, and a case-folding or Unicode question in any of
those places is a question about whether two devices agree on authority.

A fourth namespace would need a protocol version bump, because charters, evidence policies, and
the ESP32 mirror all pattern-match on these prefixes.

### Examples

```text
sense.temperature      act.relay              routine.water_cycle
sense.humidity         act.motor              routine.inspect
sense.soil_moisture    act.servo              routine.return_home
sense.camera           act.water_valve        routine.scan_room
sense.gps              act.relay_1            routine.dock
sense.distance                                routine.capture_environment
                                              routine.emergency_shutdown
```

### Correct and incorrect

```text
correct     act.water_valve, duration 10 seconds
incorrect   GPIO17 = HIGH
```

The second form is not merely discouraged — there is no field in a `CapabilityRequest` through
which it could be expressed, and no worker holds a driver handle to send it through.

## Patterns

Charter fields that take globs (`evidence.required_for`) use a deliberately tiny matcher: exact
match, a trailing `.*` prefix match, or `*`. Nothing else, because the ESP32 mirror implements the
same rule in C.

```text
act.*            matches act.water_valve, act.relay_1
routine.*        matches routine.water_cycle
sense.camera     matches only sense.camera
*                matches everything
```

## The registry

Capabilities are registered at startup from the hardware manifest and the drivers that back it,
then read-only for the process lifetime. A descriptor declares:

| Field | Meaning |
|---|---|
| `Id` | The capability id |
| `Class` | Action class this capability carries |
| `HardwareLimits` | The **innermost limit tier** — what the device physically permits |
| `Parameters` | Accepted parameter names. Anything else is refused, never ignored |
| `RequiredParameters` | Parameters that must be supplied |
| `DurationParameter` | Which parameter `max_on_s` / `min_off_s` / `max_rate_per_h` govern — conventionally `on_s` |
| `MagnitudeParameter` | Which parameter `min`/`max` govern — a servo angle, a motor speed, a geofenced axis |
| `ParameterRanges` | Per-parameter hard ranges from the driver |
| `Available` | Whether the device is currently usable |

Registration validates rather than trusting, and a rejected descriptor is simply not registered:
requests for it are refused as `unknown_capability` rather than running with unclear bounds.

Two rules are enforced at registration because they are configuration errors that would otherwise
surface only as an actuation that should not have been allowed:

- A `sense.` capability may not be registered above `observe`. A sensor classified as actuation
  would pass an actuation ceiling it has no business passing.
- Nothing may be registered as `hazardous`. Hazardous work has no per-action authorization
  pipeline yet, so a device that claims to offer it is refused at startup, not at first use.

## Parameters

The kernel does not invent parameters. If a capability needs a duration, the descriptor declares
it required, and a request without one is refused as `missing_parameter`. An unknown parameter is
refused as `unknown_parameter` rather than dropped — a caller that misspelled `on_s` asked for
something, and quietly running without it is worse than refusing.

Values are clamped in a fixed order: driver range first (the innermost thing that knows what the
hardware accepts), then the duration limit, then the magnitude limit. Every narrowing is named in
the action record's `detail`.

## Routines

A routine is a pre-defined deterministic local behaviour. Routines let a controller delegate useful
physical work without micromanaging every low-level hardware transition across a link that may
drop mid-sequence — which is why they matter most on constrained controllers, where they are the
only work available at all.

A routine descriptor declares a stable id, an action class, the capabilities it drives, compiled
hard limits, accepted and required parameters, parameter ranges, evidence expectations,
cancellation behaviour, and safe-state behaviour.

Rules:

- A charter **enables** routines from what the build registers. It never defines one.
- A charter may narrow a routine's parameters; it can never widen the compiled boundaries.
- A routine's action class must be at least that of every capability it drives. A `benign` routine
  driving a `controlled` capability would launder the action class, and the registry refuses it.
- A routine's backing capabilities need not be separately granted in the charter's `capabilities`
  list. The routine **is** the unit of delegation — that is the point of having routines.
- A routine id appearing in a charter's `capabilities` list is a drafting error and fails
  validation, pointing at the `routines` field instead. One way to say a thing.

## Where an action record puts a routine

For a routine invocation the record carries the routine id in **both** `capability` and
`routine_id`. Evidence policies pattern-match on `capability`, so a `routine.*` policy would
otherwise never see it. Sub-actions a routine drives carry their own capability with `routine_id`
naming the routine that caused them.
