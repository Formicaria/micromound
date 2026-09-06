# Micromound.Drivers

Deterministic adapters between semantic capabilities and physical hardware.

```
Hardware  →  Driver  →  Capability  →  Ant
```

A BME280 is not an ant. It is a device, behind a driver, exposing `sense.temperature`,
`sense.humidity`, and `sense.pressure`, which a Climate Ant consumes. That indirection is what
lets new hardware be added without the runtime — or any upstream controller — learning anything
about boards, buses, or part numbers.

Drivers declare the **innermost limit tier**: what the device physically permits. Nothing above
can widen it.

A driver never decides whether an operation was allowed. By the time `ICapabilityExecutor.Execute`
is called that question is settled, and the driver has no access to the charter that settled it.

Status: M4 for the first real drivers (GPIO relay, BME280, ADS1115). Bus abstractions and the
registry are here now so the shape is fixed before any hardware depends on it.

## The three generic primitives

A line you drive (`digital_actuator`), a number you read (`analog_sensor`), and — since `v0.9.27` —
a **fact you read** (`digital_sensor`: a limit switch, an interlock contact, a float). Each is one
driver type whatever backs it; only the port changes.

The third is not a convenience. An actuator produces no evidence of its own — a command is not
evidence — so without a line the actuation path did not drive, every actuation a mound performs is
honestly `unverified` however well the hardware works. `digital_sensor` is the second observer a
mission's `verify` step reads, and it is what makes a verified outcome reachable at all.

Its one hard rule, shared by every backing: a line that cannot be sampled **throws**, and the driver
turns that into a fault with no reading. "The switch is open" and "I could not see the switch" are
different facts, and a backing that returned `false` for both would turn a dead input into a
confident measurement.

## Ports on a board over the link (`link`)

A manifest device whose settings carry `link` (a serial device such as `/dev/ttyUSB0`) puts its
line or channel on an ESP32 running the port server (PROTOCOL.md §12, port requests) instead of a
local GPIO or an ADS1115. `LinkPortsClient` speaks the four requests; `LinkDigitalOutput`,
`LinkAnalogInput` and `LinkDigitalInput` are the `IDigitalOutput`/`IAnalogInput`/`IDigitalInput`
behind the same generic drivers, so the
kernel, the limits, the safe state and the evidence are unchanged. `LinkPortsPool` opens one client
per device, says hello (a board that does not answer, does not offer the pin or channel, disagrees
with `active_high`, or is tripped refuses the manifest fail-closed), and keeps the board's watchdog
fed. The hardware factories read `link` first and fall through to the local backing when it is empty.
