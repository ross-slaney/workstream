#!/usr/bin/env bash
# Turns a new Debian 13 machine into the desktops' image. Runs once, as root, on the builder machine that a
# release makes and then captures into the gallery (.github/actions/bake-desktop-image), so a new desktop
# starts with nothing left to install and nothing left to set up.
#
#   bake.sh <github token> <owner/repository> <commit>
#
# The token reads this repository for the checkout. It is the workflow's own, which GitHub ends with the job,
# so what Azure keeps of this run is worth nothing afterwards. The image holds no keys: a desktop is told its
# own when it is given a task (api/Desktops/DesktopHosts.cs).
#
# Azure hands back only the end of what this prints, so each step writes to a file and the last lines of the
# step that failed are printed. The final line is printed only when every step ran.
set -euo pipefail
token="$1"
repository="$2"
commit="$3"
export DEBIAN_FRONTEND=noninteractive HOME=/root
# Azure starts this in a folder only root can read, which the desktop's own user is sent into by sudo.
cd /
log=/var/log/workstream-bake.log
# The steps below write to the log, and a step that fails is inside that redirection: what went wrong is
# printed to where this script's own output goes, which is what Azure hands back.
exec 3>&1
trap 'echo "The image was not built. Its last lines:" >&3; tail -c 2500 "$log" >&3' ERR

{
  apt-get update -q
  apt-get install -y -q --no-install-recommends git ca-certificates
  source=$(mktemp -d)
  git clone --quiet "https://x-access-token:${token}@github.com/${repository}.git" "$source"
  git -C "$source" checkout --quiet "$commit"
  rm -rf /opt/workstream
  cp -a "$source/desktop" /opt/workstream
  rm -rf "$source" /opt/workstream/agent/node_modules
  echo "$commit" > /opt/workstream/COMMIT

  bash /opt/workstream/image/install.sh
  bash /opt/workstream/image/configure.sh

  # The desktop runs once here, so what a first run builds (font and icon caches, XFCE's own first settings,
  # the compiled keyboard map) is in the image and no new machine spends its first seconds on it. This
  # builder is told nothing, so the agent only waits, and what the run wrote of its own is removed.
  systemctl start workstream-desktop.service
  for _ in $(seq 120); do pgrep -x xfdesktop > /dev/null && pgrep -x xfce4-panel > /dev/null && break; sleep 1; done
  pgrep -x xfdesktop > /dev/null
  sleep 15
  systemctl stop workstream-desktop.service
  rm -rf /home/work/.workstream/* /home/work/.cache/sessions /tmp/.X1-lock /tmp/.X11-unix/X1

  # What a desktop cannot do without.
  command -v Xtigervnc chromium code node docker dotnet gh xdotool sqlcmd growpart lsof nft opencode > /dev/null
  test -d /opt/workstream/agent/node_modules/@anthropic-ai/claude-agent-sdk
  systemctl is-enabled workstream-desktop.service workstream-grow-disk.service > /dev/null
  sudo -u work -H dotnet dev-certs https --check
  # The Android emulator boots as the desktop's person, and adb sees it (check-emulator.sh).
  bash /opt/workstream/image/check-emulator.sh
  # host.docker.internal is this machine, from a container and from itself (check-host-name.sh). What a
  # desktop gets at boot is put in place by hand here, on a builder that has not started again since it was
  # configured; Docker, started by the check, is stopped again.
  sysctl -q --system
  systemctl restart systemd-resolved
  systemctl start workstream-host-name.service
  bash /opt/workstream/image/check-host-name.sh
  systemctl stop docker.service
  systemctl is-enabled workstream-host-name.service > /dev/null
  # Swap in memory: the kernel has the module, and the service that makes it works (swap.sh).
  modinfo zram > /dev/null
  systemctl is-enabled workstream-swap.service > /dev/null
  bash /opt/workstream/image/swap.sh
  grep -q '^/dev/zram0 ' /proc/swaps
  swapoff /dev/zram0
  # What a desktop Azure hibernates needs: the room made at boot, the rule that answers Azure's request, and
  # the early-boot script that lets only the machine that hibernated wake up from its disk (hibernate.sh).
  command -v filefrag mkswap swapon > /dev/null
  systemctl is-enabled workstream-hibernate.service > /dev/null
  test -f /etc/udev/rules.d/90-workstream-hibernate.rules
  # grep reads the whole list: with -q it stops at the match, lsinitramfs gets a broken pipe and pipefail fails the check.
  lsinitramfs "/boot/initrd.img-$(uname -r)" | grep scripts/init-premount/workstream-resume-here > /dev/null
  test -f /home/work/.config/xfce4/xfconf/xfce-perchannel-xml/xfce4-panel.xml
  # The phones: the command, the two launchers, and agent-sim for the desktop's person (configure.sh). The
  # iPhone simulator itself is installed by an environment that wants it.
  command -v phone surfaces ffmpeg wmctrl > /dev/null
  grep -q -- --allow-insecure-localhost /etc/chromium.d/workstream
  grep -q PasswordManagerEnabled /etc/chromium/policies/managed/workstream.json
  test -f /usr/share/applications/workstream-iphone.desktop -a -f /usr/share/applications/workstream-android.desktop
  sudo -u work -H /home/work/.local/bin/agent-sim --version
  test -f /var/lib/systemd/linger/work
} > "$log" 2>&1

# Make this disk an image (seal.sh). `waagent -deprovision` would do part of it, but it stops the agent that
# reports this script's result.
bash /opt/workstream/image/seal.sh >> "$log" 2>&1
echo "WORKSTREAM IMAGE OK $commit"
