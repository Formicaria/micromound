# Constrained controller firmware (ESP32) — M5

The ESP-IDF project that puts [`firmware/micromound-c`](../micromound-c/README.md) on a board. The
software of the controller is finished and host-tested — the wire format, the reader and validators,
the capability kernel, the device loop, and (since `v0.9.22`) the board layer: enrollment, the sync
transport, the relay and probe drivers as kernel executors, and the service loop, all written over a
seven-function hardware abstraction ([`mm_hal.h`](../micromound-c/include/mm_hal.h)) and proven on
the host against a fake of it (`tests/test_board.c`). What this directory adds is the one file that
knows it is on an ESP32 — `main/hal_esp32.c` — plus the board description and `app_main`.

**Status: compiles under ESP-IDF v5.3.2 for the `esp32` target, in both link configurations; not
yet flashed or run on a board.** Every line of logic it calls has run on the host; the HAL binding
has been compiled, not exercised — enrolling against a controller and watching a beat are the bench
slice, and this README will say "runs on" only after that. The CI job `esp32` builds both images on
every push with the same IDF version.

| Configuration | Image | DRAM at link | What the board needs |
|---|---|---|---|
| **Wi-Fi + HTTPS** (`sdkconfig.defaults`) | 1,026,224 B (33% of the 1.5 MB partition free) | 41.5% used, 105 KB free for Wi-Fi and the TLS handshake | a network, the controller's certificate chain, NTP |
| **Serial link** (`sdkconfig.defaults.serial`) | 295,940 B (81% free) | 35.4% used, 116 KB free | a USB cable to a Pi running `micromound --bridge` |

`libmicromound_c.a` is 38–39 KB of flash code either way; the static `mm_app` is 40 KB of DRAM
(uplink queue of 8), plus 11 KB for the serial link's frame decoder in the serial image.

## What a board supplies

```c
typedef struct mm_hal {
    void *ctx;
    int64_t (*now)(void *ctx);                                    /* UTC epoch seconds; 0 until the clock is set  */
    int (*random_bytes)(void *ctx, uint8_t *out, size_t n);       /* the seed, envelope ids                       */
    int (*http_post_json)(void *ctx, const char *path, ...);      /* POST JSON, get status + body; -1 = offline    */
    int (*kv_get)(void *ctx, const char *key, ...);               /* protected storage: seed, controller key, token */
    int (*kv_set)(void *ctx, const char *key, ...);
    int (*gpio_write)(void *ctx, int pin, int level);
    int (*adc_read)(void *ctx, int channel, double *volts);
} mm_hal;
```

That is the whole port. `hal_esp32.c` fills it with SNTP's clock (zero until the year is plausible),
`esp_fill_random`, `esp_http_client` over esp-tls (the Mozilla root bundle, or a private CA embedded
from `main/certs/controller_ca.pem` — never an insecure mode), NVS blobs in the `micromound`
namespace, `gpio_set_level`, and `adc_oneshot` with the target's calibration scheme. Everything above
it — `mm_app`, `mm_enroll`, `mm_link`, `mm_drivers`, `mm_device`, `mm_kernel` — is the library,
compiled unchanged from `../micromound-c/src` by `components/micromound_c`.

**The serial link** (`CONFIG_MM_LINK_SERIAL`, PROTOCOL.md §12) swaps one of the seven: `http_post_json`
becomes `mm_serial_post_json` — the same path and body, framed over a UART to a Pi running
`micromound --bridge`, which performs the HTTPS half and frames the status and body back — and the
clock is asked of the bridge (`micromound/link/time`) at boot and hourly instead of SNTP. Nothing else
changes: not the identity, not the enrollment, not a byte of any envelope. The board then has no
network stack at all, which is why that image is a third the size and why the heap question of the
Wi-Fi image does not arise. By default the link is UART 0 — the USB-serial cable that flashes the
board — so the console log is off in that configuration (it would corrupt the frames); use
`CONFIG_MM_SERIAL_UART=1` with TX/RX pins to keep the console.

## What the board does

`app_main` drives the relay to its safe level before the network is up, brings up Wi-Fi
(ESP-IDF's `protocol_examples_common`, configured in menuconfig), waits for SNTP — **nothing signs or
actuates on a zero clock** — and then hands everything to `mm_app`, one tick a second:

- **Identity.** The Ed25519 seed is created from the hardware RNG on first boot and stored in NVS
  (`mm.seed`); it is never regenerated and never read out. A board whose NVS will not open, or will
  not store the seed, halts with its outputs safe rather than run with an identity that would not
  survive a reboot.
- **Enrollment** (PROTOCOL.md §3). Until a controller key is stored, every 30 s the app spends the
  one-time token in `mm.token`: an outage retries, a controller error retries, a definite 4xx
  refusal burns the token, success stores the controller's key (`mm.ctl_pk`) and cadence
  (`mm.sync_s`) and burns the token. The request body and every verdict line are the host's own,
  byte for byte (`enroll-exchange.txt`).
- **The loop.** Once enrolled: release any relay hold whose time is up, quiesce when the lease runs
  out, beat on the charter's `sync_interval_s` (enrollment's until chartered), and run the compiled
  schedule — a reading every minute, the relay for 30 s every ten minutes — through the kernel, which
  refuses what the charter does not allow and records the refusal.
- **Safety.** A stop or a quiesce releases every relay. A relay whose release write fails is a
  **trip**: the mound is stopped, the hold stays pending and is retried every tick, and the beat
  still goes out so the controller hears it. The task watchdog reboots a loop that stops returning,
  and every relay comes back up at its safe level.

## Building (when you have the toolchain)

```bash
. $IDF_PATH/export.sh
cd firmware/esp32
idf.py set-target esp32
idf.py menuconfig          # Example Connection Configuration → Wi-Fi; MicroMound controller → link, URL, mound id, token, pins
idf.py build flash monitor

# the serial-link image instead (no Wi-Fi; a Pi bridges — DEPLOY.md §7):
idf.py -B build-serial -DSDKCONFIG=sdkconfig.serial -DSDKCONFIG_DEFAULTS="sdkconfig.defaults;sdkconfig.defaults.serial" build flash
```

`main/Kconfig.projbuild` holds the controller URL, the mound id, the enrollment token (bench
provisioning: written to NVS on a boot that has neither a token nor a controller key — a burned
compiled-in token is re-provisioned and refused again once per boot, harmlessly), the NTP server,
the relay GPIO and polarity, the probe's ADC channel and its volts→reading scale and offset, and the
tick period. For a fleet, provision `mm.token` in NVS directly and leave the Kconfig token empty.
A controller with a private CA: put its PEM in `main/certs/controller_ca.pem` (the file is embedded;
empty means "use the root bundle").

## Layout

```text
firmware/esp32/
  CMakeLists.txt                 the project; pulls protocol_examples_common for Wi-Fi; MM_DEVICE_QUEUE=8 project-wide
  sdkconfig.defaults             1.5 MB app partition, 32 KB main-task stack, task watchdog (panic → reboot → safe), TLS bundle
  sdkconfig.defaults.serial      overlay: the serial link on UART 0, console off
  main/
    app_main.c                   boot order, clock wait, the tick loop, the watchdog
    hal_esp32.c / .h             mm_hal over ESP-IDF — the only file that knows the board; both links live here
    board.c / .h                 THIS board: capability tables, relay on a GPIO, probe on an ADC channel, the schedule
    Kconfig.projbuild            the menuconfig entries above
    certs/controller_ca.pem      optional private CA (empty = root bundle)
  components/micromound_c/       ../../micromound-c, compiled as an IDF component, unchanged
```

## What is still ahead

- **The bench run.** Flash, enroll against a controller, watch a beat, read the heap high-water mark
  through a TLS exchange. The first slice of real hardware, and the one that turns this README's
  "compiles" into "runs on".
- **Routing through a Pi-class mound's own kernel.** The serial link makes a Pi the board's
  transport; it does not make the Pi the board's authority. A board whose actions are requested by
  a Pi's Forager and gated by the Pi's kernel — the "generic driver sends a bounded request to the
  ESP32" of the acceptance bench — is a different arrangement, on top of this link, not yet designed.
- **Layer 0.** E-stops and interlocks wired outside the MCU's control, reported as observed facts
  only (SAFETY.md). Nothing here pretends to be one.
