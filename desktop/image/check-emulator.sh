#!/usr/bin/env bash
# Checks, as root, that the Android emulator works as the desktop's person: a phone of its own, made the way
# every desktop's Phone is (phone.sh), starts on the processor's virtualization (QEMU holds /dev/kvm), boots
# until Android says it has (sys.boot_completed), and adb lists it as a device. Run when the image is baked
# (bake.sh), with the command the agent is told to use (agent/instructions.mjs), less its window.
#
# It is started from the person's home. From a folder they cannot read, such as the one an Azure run command
# starts in, the emulator stops at once ("QEMU main loop exits abnormally with code 1"), whoever it runs as.
# `emulator -accel-check` passes there all the same, so it proves nothing by itself.
#
# The phone and everything the run wrote are removed, so a desktop's own Phone has never booted.
set -euo pipefail
# An image for a processor Android's emulator has no system image for comes without one.
[ -d /opt/android-sdk/system-images ] || { echo "No Android system image here: nothing to check."; exit 0; }
test -d /home/work/.android/avd/Phone.avd
sudo -u work -H bash -l <<'WORK'
set -euo pipefail
cd ~
emulator -accel-check > /dev/null
/opt/workstream/image/phone.sh Check
log=/tmp/emulator-check.log
emulator -avd Check -no-window -gpu swiftshader_indirect -no-audio -no-snapshot -no-metrics -no-nested-warnings -no-boot-anim > "$log" 2>&1 &
failed() { echo "The Android emulator did not $1. Its last lines:"; tail -20 "$log"; pkill -u work "^qemu-system" || true; exit 1; }
started=$(date +%s)
held=no
for _ in $(seq 60); do
  qemu=$(pgrep -u work "^qemu-system" | head -1 || true)
  if [ -n "$qemu" ] && ls -l "/proc/$qemu/fd" 2> /dev/null | grep -q /dev/kvm; then held=yes; break; fi
  sleep 1
done
[ "$held" = yes ] || failed "start on the processor's virtualization"
booted=no
for _ in $(seq 150); do
  if [ "$(adb shell getprop sys.boot_completed 2> /dev/null | tr -d "\r")" = 1 ]; then booted=yes; break; fi
  kill -0 "$qemu" 2> /dev/null || failed "keep running"
  sleep 2
done
[ "$booted" = yes ] || failed "boot in five minutes"
adb devices | grep -q "^emulator-[0-9]*[[:space:]]*device$" || failed "show up in adb"
echo "The Android emulator booted as $(id -un) in $(( $(date +%s) - started )) seconds."
adb emu kill > /dev/null 2>&1 || true
for _ in $(seq 20); do pgrep -u work "^qemu-system" > /dev/null || break; sleep 1; done
pkill -u work "^qemu-system" || true
adb kill-server > /dev/null 2>&1 || true
avdmanager delete avd -n Check > /dev/null
rm -f "$log"
rm -rf ~/.android/avd/running
find ~/.android -mindepth 1 -maxdepth 1 ! -name avd -exec rm -rf {} +
WORK
