#!/usr/bin/env bash
# Install or upgrade the MicroMound daemon on a Raspberry Pi / Linux SBC as a systemd service.
#
#   sudo bash deploy/install.sh [path/to/micromound-<version>-linux-arm64.tar.gz]
#
# With an archive: unpacks the single-file `micromound` binary into /opt/micromound. Without one:
# expects ./micromound (the published binary) beside this script's parent, e.g. an unpacked release.
# Idempotent: re-running upgrades the binary and unit, keeps /etc/micromound and /var/lib/micromound.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then echo "run as root: sudo bash $0 $*" >&2; exit 2; fi

here="$(cd "$(dirname "$0")" && pwd)"
archive="${1:-}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

if [ -n "$archive" ]; then
  tar -xzf "$archive" -C "$stage"
  bin="$stage/micromound"
else
  bin="$here/../micromound"
  [ -x "$bin" ] || bin="$here/micromound"
fi
[ -x "$bin" ] || { echo "no micromound binary found (pass the release .tar.gz)" >&2; exit 2; }

# A dedicated, unprivileged user. gpio and i2c are the device groups Raspberry Pi OS uses for
# /dev/gpiochip* and /dev/i2c-*; they exist on Pi OS, and are created here if this is not Pi OS.
id -u micromound >/dev/null 2>&1 || useradd --system --home-dir /var/lib/micromound --shell /usr/sbin/nologin micromound
for g in gpio i2c; do getent group "$g" >/dev/null || groupadd --system "$g"; done
usermod -a -G gpio,i2c micromound

install -d -m 0755 /opt/micromound
install -m 0755 "$bin" /opt/micromound/micromound
install -d -o micromound -g micromound -m 0750 /var/lib/micromound
install -d -m 0750 -o root -g micromound /etc/micromound
[ -f /etc/micromound/micromound.env ] || install -m 0640 -o root -g micromound "$here/micromound.env" /etc/micromound/micromound.env
install -m 0644 "$here/micromound.service" /etc/systemd/system/micromound.service
systemctl daemon-reload

# I2C must be enabled in firmware on a Pi; say so rather than let the daemon refuse at 3 a.m.
if [ -e /boot/firmware/config.txt ] || [ -e /boot/config.txt ]; then
  cfg=/boot/firmware/config.txt; [ -e "$cfg" ] || cfg=/boot/config.txt
  if ! grep -Eq '^\s*dtparam=i2c_arm=on' "$cfg"; then
    echo "NOTE: I2C is not enabled in $cfg. For an ADS1115 run: sudo raspi-config nonint do_i2c 0   (then reboot)"
  fi
fi

cat <<MSG

Installed /opt/micromound/micromound ($(/opt/micromound/micromound --describe-drivers 2>/dev/null | grep -c driver_type || echo '?') driver types).

Next:
  1. Put the manifest ANTHILL authored at /etc/micromound/manifest.json (root:micromound 0640).
  2. Edit /etc/micromound/micromound.env: controller URL and the one-time enrollment token.
  3. Check the wiring against the manifest — nothing is actuated:
       sudo -u micromound /opt/micromound/micromound --manifest /etc/micromound/manifest.json --check-hardware
  4. sudo systemctl enable --now micromound && journalctl -u micromound -f
MSG
