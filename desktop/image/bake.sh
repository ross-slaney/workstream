#!/usr/bin/env bash
# Turns a new Debian 13 machine into the desktops' image. Runs once, as root, on the builder machine that
# .github/workflows/release.yml makes and then captures into the gallery (infra/foundation.bicep), so a new
# desktop starts with nothing left to install.
#
#   bake.sh <github token> <owner/repository> <commit>
#
# The token reads this repository for the checkout. It is the workflow's own, which GitHub ends with the job,
# so what Azure keeps of this run is worth nothing afterwards. The image holds no keys: a desktop is told its
# own when it is made (api/Desktops/DesktopHosts.cs).
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
trap 'echo "The image was not built. Its last lines:"; tail -c 2500 "$log"' ERR

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
} > "$log" 2>&1

# Make this disk an image: no machine identity, and none of the account the workflow built it with.
# (`waagent -deprovision` does the same but stops the agent that reports this script's result.)
{
  apt-get clean
  userdel --remove --force builder 2>/dev/null || true
  rm -f /etc/ssh/ssh_host_* /root/.bash_history /home/work/.bash_history
  # Azure keeps this script with its arguments, the token among them, beside its output.
  rm -f /var/lib/waagent/run-command/download/*/script.sh
  cloud-init clean --logs --seed --machine-id
} >> "$log" 2>&1
echo "WORKSTREAM IMAGE OK $commit"
