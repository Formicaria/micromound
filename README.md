# OPERATION MICROMOUND

MICROMOUND extends [ANTHILL](https://github.com/Formicaria/Anthill) into physical devices:
Raspberry Pis, ESP32s, sensors, cameras, robots, fabrication equipment, and building systems.

The Primary Colony (the ANTHILL install) remains in command, delegating missions, permissions,
and operating limits to smaller Micromound colonies. Each Micromound runs either an intelligent
**Edge Queen** (Pi-class) or a simple **Deterministic Controller** (ESP32-class), depending on
its hardware.

Micromounds continue authorized work when disconnected, retain mission context, record evidence,
and synchronize with ANTHILL once reconnected. Losing communication never grants additional
authority; hazardous actions remain prohibited without explicit authorization.

Commands alone do not prove that physical work succeeded. Micromounds verify results with sensor
data, telemetry, images, or other evidence. Independent safety systems — emergency stops,
watchdogs, interlocks, operating limits — remain separate from AI control.

One colony directs the mission. Many colonies extend its reach. Each may act locally, but only
within the authority it has been given.

## Status

Pre-M0. The protocol contracts, simulator, and authority tests exist; nothing physical ships
yet, and the ANTHILL-side integration (Integrations tab) lands in M1. See
[`docs/MICROMOUND.md`](docs/MICROMOUND.md) for the phase plan.

## Layout

```text
src/Micromound.Protocol/     Shared wire contracts: envelopes, charters, evidence, validation
src/Micromound.Sim/          Simulated mounds — protocol work without hardware
src/Micromound.EdgeQueen/    Pi-class runtime (M2 scaffold)
firmware/esp32/              Deterministic Controller firmware (M3 placeholder)
tests/Micromound.Tests/      Contract, authority, and chain tests (network-free)
docs/                        Design doc, protocol spec, safety model
```

## Build and test

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0), same as ANTHILL.

```bash
dotnet build Micromound.sln
dotnet test Micromound.sln
dotnet run --project src/Micromound.Sim     # simulated mound smoke run
```

## Documentation

- [`docs/MICROMOUND.md`](docs/MICROMOUND.md) — canonical design doc: tiers, authority model, phase plan
- [`docs/PROTOCOL.md`](docs/PROTOCOL.md) — wire protocol: envelopes, charters, leases, evidence, endpoints
- [`docs/SAFETY.md`](docs/SAFETY.md) — safety model; where documents disagree, this one wins

## License

[Apache License 2.0](LICENSE)
