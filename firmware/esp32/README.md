# Constrained controller firmware (ESP32) — M5

The ESP-IDF project that puts [`firmware/micromound-c`](../micromound-c/README.md) on a board. The
software of the controller is finished and host-tested — the wire format, the reader and validators,
the capability kernel, the device loop, and (since `v0.9.22`) the board layer: enrollment, the sync
transport, the relay and probe drivers as kernel executors, and the service loop, all written over a
seven-function hardware abstraction ([`mm_hal.h`](../micromound-c/include/mm_hal.h)) and proven on
the host against a fake of it (`tests/test_board.c`). What this directory adds is the one file that
knows it is on an ESP32 — `main/hal_esp32.c` — plus the board description and `app_main`.

**Status: compiles under ESP-IDF v5.3.2 for the `esp32` target; not yet flashed or run on a board.**
`idf.py build` produces `micromound_esp32.bin` — 1,025,484 bytes (33% of the 1.5 MB app partition
free), with `libmicromound_c.a` at 37.8 KB of flash code and the static `mm_app` at 40 KB of DRAM
(uplink queue of 8; DRAM 41.5% used at link, 105 KB left for Wi-Fi and the TLS handshake). The CI job
`esp32` builds it on every push with the same IDF version. Every line of logic it calls has run on
the host; the HAL binding has been compiled, not exercised — enrolling against a controller,
watching a beat, and reading the heap high-water mark under TLS are the bench slice, and the README
will say "runs on" only after that.

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
idf.py menuconfig          # Example Connection Configuration → Wi-Fi; MicroMound controller → URL, mound id, token, pins
idf.py build flash monitor
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
  main/
    app_main.c                   boot order, clock wait, the tick loop, the watchdog
    hal_esp32.c / .h             mm_hal over ESP-IDF — the only file that knows the board
    board.c / .h                 THIS board: capability tables, relay on a GPIO, probe on an ADC channel, the schedule
    Kconfig.projbuild            the menuconfig entries above
    certs/controller_ca.pem      optional private CA (empty = root bundle)
  components/micromound_c/       ../../micromound-c, compiled as an IDF component, unchanged
```

## What is still ahead

- **The bench run.** Flash, enroll against a controller, watch a beat, read the heap high-water mark
  through a TLS exchange. The first slice of real hardware, and the one that turns this README's
  "compiles" into "runs on".
- **The Pi↔ESP32 packet protocol** (ROADMAP M5), for a controller subordinate to a Pi-class mound
  rather than enrolled directly upstream.
- **Layer 0.** E-stops and interlocks wired outside the MCU's control, reported as observed facts
  only (SAFETY.md). Nothing here pretends to be one.
