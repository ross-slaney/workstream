#!/usr/bin/env bash
# Makes a phone for the Android emulator, as the desktop's person: `phone.sh` makes the one every desktop
# comes with, Phone; `phone.sh Check` makes the throwaway one the image is checked with (check-emulator.sh),
# the same in everything but its name.
#
# avdmanager's own defaults are two processors and 2 GB. With the software renderer a desktop has, that
# phone took three minutes to start and came up with "System UI isn't responding". With four processors and
# 4 GB it reaches its home screen in under a minute. The hardware keyboard is on, so what a person types in
# the browser reaches the phone.
set -euo pipefail
name=${1:-Phone}
# shellcheck disable=SC1091
. /etc/profile.d/workstream-tools.sh
echo no | avdmanager create avd --force -n "$name" -k "system-images;android-36;google_apis;x86_64" -d pixel_7 > /dev/null
config="$HOME/.android/avd/$name.avd/config.ini"
setting() {
  if grep -q "^$1=" "$config"; then sed -i "s|^$1=.*|$1=$2|" "$config"; else echo "$1=$2" >> "$config"; fi
}
setting hw.cpu.ncore 4
setting hw.ramSize 4096M
setting hw.keyboard yes
