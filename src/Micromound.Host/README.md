# Micromound.Host

The headless Raspberry Pi / Linux daemon. Ships as `micromound.service`; requires no browser and
no graphical environment.

Composition order is the build order, and it is deliberate — nothing that can move hardware is
constructed before the thing that authorizes it:

```
identity → manifest → drivers → registries → kernel → evidence → sync → reasoning → runtime → watchdog
```

Local layout:

```
/etc/micromound/mound.json     bootstrap: identity, controller key, endpoint, hardware manifest
/var/lib/micromound/identity/  device keypair — never transmitted, never exported
/var/lib/micromound/state/     active charter, mission, lease, worker state
/var/lib/micromound/evidence/  local evidence store
/var/lib/micromound/queue/     durable outbound queue
```

All user-facing configuration and visualization belongs to the upstream controller — see
`docs/UPSTREAM.md`.

Status: M4.

## `--bridge`: the Pi side of the serial link

`micromound --bridge /dev/ttyUSB0 --controller https://anthill.example` runs only the bridge of
PROTOCOL.md §12: request frames read from the device are relayed to the controller over HTTPS byte
for byte, the status and body framed back, the board's clock requests answered from this host's
clock, and anything outside `micromound/v0/` refused with 404. No mound comes up, no key is loaded;
`LinkFrame` (the framing, pinned by `link-frames.txt`) and `LinkBridge` (the relay) are the whole of
it. The device must be in raw mode (`stty ... raw -echo`); the daemon opens it as a plain file, so
any byte stream with a path works, including a pseudo-terminal in a test.
