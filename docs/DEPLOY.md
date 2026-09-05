# Deploying a mound on a Raspberry Pi

This is the walk from "a Pi, a relay board, an ADS1115 and a soil probe on the bench" to "a mound
ANTHILL can see, chartered, running unattended". Every step is checkable before the next one; none
of them actuates anything until the last.

Everything here is `v0.9.x` substrate: the ports are proven against fakes and the kernel headers,
and **this document is the on-hardware verification** that `v0.10.0` waits for. When you finish
step 6 and the relay clicks when — and only when — the mission says so, that is the M4 boundary.

## 0. What you need

- A Raspberry Pi running a **64-bit** Raspberry Pi OS (Bookworm or later; the release ships
  `linux-arm64`, and the Pi 3, 4, 5 and Zero 2 W all run it). A 32-bit OS has no build.
- A relay module on a GPIO pin. Note whether it is **active-low** (most cheap relay boards are:
  the relay closes when the input is pulled LOW). Get this wrong and "safe" is "on".
- An ADS1115 breakout on the I2C header pins (SDA → GPIO 2, SCL → GPIO 3, VDD → 3V3, GND → GND;
  ADDR → GND gives address `0x48`), and an analog sensor into AIN0 whose output stays **below
  3.3 V** — the ADC's gain setting is resolution, not protection.
- ANTHILL reachable over HTTPS from the Pi, with a mound minted for this device (its **Micromound**
  page → *Adopt a device*). Copy the one-time token; it is shown once.

## 1. Enable the buses

```bash
sudo raspi-config nonint do_i2c 0        # dtparam=i2c_arm=on
sudo reboot
ls -l /dev/gpiochip* /dev/i2c-*           # both present; groups gpio and i2c own them
sudo apt install -y i2c-tools gpiod
i2cdetect -y 1                            # the ADS1115 shows as 48
gpioinfo | head                           # your header lines, none marked [used] by another program
```

If `i2cdetect` shows nothing at `48`, stop here: wiring or the ADDR pin. If a GPIO line shows a
consumer already, something else holds it and the daemon will refuse it with `EBUSY`.

## 2. Install the daemon

Download the `micromound-<version>-linux-arm64.tar.gz` asset from the release, then:

```bash
tar -xzf micromound-*-linux-arm64.tar.gz
sudo bash deploy/install.sh              # creates the micromound user (groups gpio, i2c), /opt, /etc, /var/lib
```

The installer prints what it did and the next steps. It does **not** start anything.

## 3. Author the manifest in ANTHILL, put it on the Pi

On ANTHILL's Micromound page, with your mound selected, *Manifest → Attached hardware*: add an
**On/off output** (capability `act.water_valve`, GPIO pin e.g. `17`, longest single run e.g. `20`,
and under Advanced *Active when high* = **no** for an active-low relay) and an **Analog sensor**
(capability `sense.soil_moisture`, ADC input `0`, unit `pct`, scale/offset if you know them). The
form asks for exactly the settings the daemon reads — it is generated from the daemon's own
catalog (`micromound --describe-drivers` prints the same thing).

ANTHILL queues the signed manifest for the mound's next beat; you also want it as a file for the
hardware check and for an offline start. Save the same manifest as
`/etc/micromound/manifest.json` (`sudo install -m 0640 -o root -g micromound manifest.json /etc/micromound/`).
A minimal one, by hand:

```json
{
  "manifest_id": "mf-workshop-1", "mound_id": "mm-workshop", "issued_at": "2026-09-05T12:00:00Z",
  "safe_state": "all_actuators_off",
  "hardware": {
    "irrigation": { "driver": "digital_actuator",
                    "settings": { "capability": "act.water_valve", "pin": "17", "active_high": "false", "max_on_s": "20" } },
    "soil":       { "driver": "analog_sensor",
                    "settings": { "capability": "sense.soil_moisture", "channel": "0", "unit": "pct", "scale": "50" } }
  },
  "capabilities": ["act.water_valve", "sense.soil_moisture"]
}
```

`mound_id` must be the id the token was minted for — ANTHILL looks the device up by the token and
cross-checks this.

## 4. Check the wiring — nothing moves

```bash
sudo -u micromound /opt/micromound/micromound --manifest /etc/micromound/manifest.json --check-hardware
```

```
micromound: hardware check (real ports, GPIO via chardev)
  OK    irrigation       digital_actuator   act.water_valve          output line claimed and held at its SAFE level (not actuated)
  OK    soil             analog_sensor      sense.soil_moisture      claimed; first reading 41.2 pct
  all 2 device(s) claimed. Nothing was actuated; no mound was composed.
```

Watch the relay while this runs: it must **not** click. If it clicks on, `active_high` is wrong
for your board — fix the manifest, not the wiring. Cover or wet the probe and run again; the
reading should move the right way. A `FAIL` line carries the driver's own reason (`EBUSY`,
`errno 2`, a channel out of range) — this is the same refusal the daemon would give at bring-up,
without the daemon.

Exit code 0 means every device was claimed. `gpioinfo` during the check shows the line as
`consumer=micromound`; after it, the kernel has released it.

## 5. Enroll and start

Edit `/etc/micromound/micromound.env`: the controller URL and the token. Then:

```bash
sudo systemctl enable --now micromound
journalctl -u micromound -f
```

The first lines say what happened: `enrolled` (the controller key is now persisted; the token is
burned and ignored from here on), the port banner `Ports: REAL hardware (GPIO via chardev,
ADS1115/I2C)`, and the watchdog. Within one sync interval ANTHILL's fleet table shows the mound
**online** and its capabilities. If enrollment is refused, the log carries ANTHILL's reason (a
burned token, a mound-id mismatch, a tier it does not accept) — nothing to retry until it is fixed.

The daemon runs with no charter: **observe only**. It will read the probe when asked and refuse
every actuation with `no_charter`. That is the correct resting state.

## 6. Charter, then one mission — the M4 boundary

In ANTHILL: *Charter* — the capability list is pre-filled from what the device reported; ceiling
`benign`; issue. Then *Physical mission* → *Load the watering example* → *Dispatch*. The mound
collects both on its next beat and runs: read the probe, open the valve for 10 s **only if** the
reading was below 30, read again, verify.

What you should see, in order: the relay clicks on exactly once, stays on ~10 s (plus up to one
tick interval — the release runs on the service loop), clicks off; ANTHILL's *Mission evidence*
shows the device's report **and** the colony's own verification from the two readings, never
merged. Pull the network cable during the hold: the valve still releases on time (the hold is the
device's clock, not the controller's), and when the lease runs out the mound quiesces to
`all_actuators_off` and refuses new actuations until a fresh charter arrives.

Then `sudo systemctl kill -s SIGKILL micromound` while a hold is active. The relay releases (the
kernel drops the line to its default — check that your board's idle state is *off*, SAFETY.md
Layer 0), systemd restarts the daemon in 3 s, it requests the line at the safe level, and the
mission is reported as not proven finished rather than resumed.

If all of that held, the host has run on a real device against real hardware: `v0.10.0`.

## Operating notes

- **Stop.** ANTHILL's *Stop* on the fleet row is carried by the next beat; the mound de-energizes,
  persists the stop, and stays stopped across restarts until *Resume*. Locally,
  `systemctl stop micromound` is a safe shutdown (SIGTERM → safe state → persist).
- **Logs.** `journalctl -u micromound`. Refusals are one line each with the reason; the daemon
  never logs a secret.
- **State** is `/var/lib/micromound`: `identity/` (the device key — back it up nowhere, re-enroll
  instead), `state/`, `evidence/`. Use endurance media for a long-lived deployment.
- **Legacy GPIO.** A kernel without `/dev/gpiochip*` can use `--gpio sysfs`; uncomment the
  `ReadWritePaths=/sys/class/gpio` line in the unit. Pin numbers are then global sysfs numbers.
- **Development machine.** To run a manifest that names physical ports with no hardware, pass
  `--simulate`; the daemon says `SIMULATING` and every port is in memory. Without `--hardware` or
  `--simulate` such a manifest is refused, so a device can never fake its readings by accident.
