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
