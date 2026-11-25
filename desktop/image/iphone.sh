#!/usr/bin/env bash
# Adds the iPhone simulator to a machine made from the desktops' image, as root, so the machine can be captured
# as the image an environment with the iPhone starts from (.github/actions/add-iphone-to-image, the gallery's
# machine-iphone). Installing it is `phone ios setup` (agent/phone.mjs): x86Sim, Apple's iOS runtime from
# Apple, and one phone, booted once so that its first, slow start is in the image. About twenty minutes and
# 30 GB, which is why it is its own image and not part of every desktop's.
#
# Azure hands back only the end of what this prints, so the steps write to a file and the last lines of a step
# that failed are printed. The final line is printed only when every step ran.
set -euo pipefail
cd /
log=/var/log/workstream-iphone.log
exec 3>&1
trap 'echo "The iPhone was not added to the image. Its last lines:" >&3; tail -c 2500 "$log" >&3' ERR

{
  # The machine booted as a desktop that was told nothing, so its agent only waits. It is stopped: nothing of
  # a session belongs in the image.
  systemctl stop workstream-desktop.service || true
  # The phone runs as a service of the person's own systemd, which is kept running without a login.
  uid=$(id -u work)
  loginctl enable-linger work
  for _ in $(seq 60); do [ -S "/run/user/$uid/systemd/private" ] && break; sleep 1; done

  sudo -u work -H bash -lc "phone ios setup"

  # The phone is there, and shut down again.
  test -d /home/work/.local/share/x86sim/var
  sudo -u work -H env XDG_RUNTIME_DIR="/run/user/$uid" /home/work/.local/bin/agent-sim status | grep -q "^p[[:space:]]"
  sudo -u work -H env XDG_RUNTIME_DIR="/run/user/$uid" /home/work/.local/bin/agent-sim down || true
  rm -rf /home/work/.workstream/* /home/work/.cache/sessions /tmp/.X1-lock /tmp/.X11-unix/X1
  df -h / | tail -1
} > "$log" 2>&1

# Make this disk an image again (seal.sh): the machine it was made from had been one.
bash /opt/workstream/image/seal.sh >> "$log" 2>&1
echo "WORKSTREAM IPHONE IMAGE OK"
