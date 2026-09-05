# Configuration

MicroMound is configured declaratively. A **mound manifest** describes what hardware is attached,
what capabilities that hardware exposes, any *optional* specialized workers to add, which routines
are available, what the operator's own limits are, and whether reasoning is enabled.

The manifest specializes a mound to its hardware **through capabilities**, not by defining its
colony: the standard Mound Major and six default ants are always present and are not declared here.
The `workers:` block below is only for *optional* application-specific ants a deployment chooses to
add on top of that default roster (see [`ANTS.md`](ANTS.md)); a mound that needs none omits the
block entirely and still runs a complete colony. The device-specific workers in the example are
illustrations of that extension point, not built-in types.

Configuration is validated before activation and **fails closed**: an unparseable or internally
inconsistent manifest leaves the previous manifest in force and the refusal is reported.

## Where a manifest comes from

Two paths, same contract:

- **Delivered.** The upstream controller signs a `config` envelope carrying the manifest. This is
  the normal path — all user-facing configuration belongs to the controller, so an operator edits
  a form there and the mound receives the result. See [`UPSTREAM.md`](UPSTREAM.md).
- **Local.** `/etc/micromound/mound.json` on a standalone mound, or as bootstrap before first
  enrollment.

Bootstrap configuration stays minimal: mound identity, trusted controller public key, transport
endpoint, hardware manifest, declared safe state, device credentials.

## Shape

The wire form is JSON (`MoundManifest` in `Micromound.Protocol`). Shown here in YAML for
readability; the fields are identical.

```yaml
manifest_id: 0f2c…
mound_id: greenhouse-01
issued_at: 2026-08-14T21:04:11Z

hardware:
  soil:
    driver: analog_sensor            # an ADS1115 channel when the daemon runs with --hardware
    settings:
      capability: sense.soil_moisture
      channel: "0"
      gain: "4.096"
      scale: "50"                    # 0..2 V probe → 0..100 %
      unit: pct
  temperature:
    driver: bme280                   # illustrative: a chip-specific driver a future port would add
    settings:
      bus: "1"
      address: "0x76"
  irrigation:
    driver: digital_actuator         # a GPIO line (/dev/gpiochip0) when the daemon runs with --hardware
    settings:
      capability: act.water_valve
      pin: "17"
      active_high: "false"
      max_on_s: "20"

capabilities:
  - sense.soil_moisture
  - sense.temperature
  - sense.humidity
  - act.water_valve

routines:
  - routine.water_cycle

workers:
  - name: Soil Ant
    purpose: soil moisture observation and trend
    runtime_type: sensor
    consumes: [sense.soil_moisture]
    action_ceiling: observe
    offline_behaviour: continue
  - name: Watering Ant
    purpose: irrigation within charter limits
    runtime_type: actuator
    consumes: [act.water_valve, routine.water_cycle]
    action_ceiling: benign
    required_evidence: [soil_before, soil_after]
    offline_behaviour: drain

device_limits:
  act.water_valve:
    max_on_s: 20

reasoning:
  mode: none

safe_state: all_actuators_off
```

## Driver settings are strings

Every value under `settings` is a string, and each driver parses and validates its own. That keeps
the manifest decoder one fixed shape — which matters on a constrained device — and puts the
knowledge of what a legal pin, bus address, or channel is in the driver, where it belongs. A
driver that cannot make sense of its settings fails to initialize rather than initializing wrong.

## The shipped driver types and their settings

Two generic primitives ship today. Each is one driver type whatever backs it: in-memory in the
simulator and tests, real Linux ports when the daemon runs with `--hardware`. The settings a real
port needs are simply ignored by the in-memory backing, so one manifest serves both.

| Driver type | Setting | Required | Meaning |
|---|---|---|---|
| `digital_actuator` | `capability` | yes | The `act.` capability this line is (`act.water_valve`) |
| | `active_high` | no (`true`) | Whether the active level is high; the safe level is the opposite |
| | `class` | no (`benign`) | Action class; never `observe` or `hazardous` |
| | `max_on_s`, `min_off_s`, `max_rate_per_h` | no | The hardware limit tier for this line |
| | `pin` | with `--hardware` | The GPIO line (BCM numbering on a Pi): a chip line offset on the character device, the global number on sysfs |
| | `chip` | no (`0`) | `/dev/gpiochip<chip>` (character device only; the Pi header is chip 0, chip 4 on a Pi 5 with an older kernel). The sysfs backing refuses a non-zero chip |
| `analog_sensor` | `capability` | yes | The `sense.` capability this channel is (`sense.soil_moisture`) |
| | `unit` | no | Unit recorded on every reading (`pct`, `V`, `C`) |
| | `scale`, `offset` | no (`1`, `0`) | Linear calibration, `value = raw × scale + offset`; both must be finite |
| | `channel` | with `--hardware` | ADS1115 input, `0`..`3` (single-ended against GND) |
| | `bus` | no (`1`) | I2C bus number: `/dev/i2c-<bus>`; the Pi's header bus is 1 |
| | `address` | no (`0x48`) | 7-bit I2C address, decimal or `0x` hex; the ADS1115 offers `0x48`..`0x4B` by its ADDR pin |
| | `gain` | no (`4.096`) | PGA full-scale range in volts: `6.144`, `4.096`, `2.048`, `1.024`, `0.512`, `0.256`. Resolution, not protection: inputs must stay below VDD + 0.3 V |

A real backing reads in **volts** before calibration. A malformed or missing setting a backing
needs, or a chip that does not answer at its address, refuses the whole manifest at bring-up — the
daemon never comes up with a phantom sensor or an unbacked line.

This table is also **machine-readable**: `Micromound.Protocol.DriverSchemaCatalog` carries the same
settings with labels, help text, kinds, defaults, bounds, and an `advanced` flag, per driver type.
The daemon prints it with `micromound --describe-drivers`, and sends it to the controller at
enrollment (`driver_schemas`, PROTOCOL.md §3), so a controller can generate its hardware form from
the device's own description instead of hand-matching setting names. A test pins the catalog to the
drivers: every setting a driver reads is described, and every default in the catalog is the
driver's real default.

## `device_limits` is the middle tier

This is the operator's own bound, and it is the reason there are three tiers rather than two:

```text
hardware/firmware   ∩   device_limits   ∩   charter limits   =   effective
```

It sits between what the hardware can physically do and what any charter grants, so an operator
can permanently narrow a device below its hardware ceiling — a pump on a smaller reservoir this
season, a servo restricted after a mount was changed — independent of whatever a later mission
asks for. A charter cannot undo it, and the attempt is reported.

## What validation checks

- `mound_id` matches this device
- `manifest_id` and `issued_at` are present and parseable
- every driver named is available in this build
- every capability id is well formed
- every routine id is in the `routine.` namespace
- worker names are unique
- no worker declares a `hazardous` ceiling
- every worker's `offline_behaviour` is one of `continue`, `drain`, `suspend`
- every worker's `runtime_type` is one of `deterministic`, `algorithmic`, `sensor`, `actuator`,
  `reasoning`
- no worker requires reasoning while `reasoning.mode` is `none`
- every capability a worker consumes **or exposes** is declared by this mound
- every `device_limits` key matches a declared capability or routine
- `safe_state` is present

A `device_limits` entry for a capability that does not exist is an error rather than a no-op,
because silently ignoring it is how an operator comes to believe a bound is in force when it
is not. An undeclared `exposes` entry is refused for the same reason from the other direction: it
reads to every other worker as an available capability and resolves to nothing.

`runtime_type` is a closed set rather than a label, because "ant does not mean language model"
(MICROMOUND.md design rule 9) is only enforceable if a manifest cannot invent a kind whose meaning
nothing agrees on. `reasoning` is the only value that implies a model, and even that one only
proposes — the capability kernel remains authoritative regardless of what a worker calls itself.

## Local layout

```text
/etc/micromound/mound.json       bootstrap: identity, controller key, endpoint, hardware
/var/lib/micromound/identity/    device keypair — never transmitted, never exported
/var/lib/micromound/state/       active charter, mission, lease, worker state
/var/lib/micromound/evidence/    local evidence store
/var/lib/micromound/queue/       durable outbound queue
```

The runtime requires no browser and no graphical environment. It runs as `micromound.service`.
