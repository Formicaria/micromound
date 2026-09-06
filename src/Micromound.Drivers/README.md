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

## Ports on a board over the link (`link`)

A manifest device whose settings carry `link` (a serial device such as `/dev/ttyUSB0`) puts its
line or channel on an ESP32 running the port server (PROTOCOL.md §12, port requests) instead of a
local GPIO or an ADS1115. `LinkPortsClient` speaks the three requests; `LinkDigitalOutput` and
`LinkAnalogInput` are the `IDigitalOutput`/`IAnalogInput` behind the same generic drivers, so the
kernel, the limits, the safe state and the evidence are unchanged. `LinkPortsPool` opens one client
per device, says hello (a board that does not answer, does not offer the pin or channel, disagrees
with `active_high`, or is tripped refuses the manifest fail-closed), and keeps the board's watchdog
fed. The hardware factories read `link` first and fall through to the local backing when it is empty.
