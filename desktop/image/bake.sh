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

  # What a desktop cannot do without.
  command -v Xtigervnc chromium code node docker dotnet gh xdotool > /dev/null
  test -d /opt/workstream/agent/node_modules/@anthropic-ai/claude-agent-sdk
  systemctl is-enabled workstream-desktop.service > /dev/null
  test -f /home/work/.config/xfce4/xfconf/xfce-perchannel-xml/xfce4-panel.xml
} > "$log" 2>&1

# Make this disk an image (seal.sh). `waagent -deprovision` would do part of it, but it stops the agent that
# reports this script's result.
bash /opt/workstream/image/seal.sh >> "$log" 2>&1
echo "WORKSTREAM IMAGE OK $commit"
