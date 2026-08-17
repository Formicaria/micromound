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
