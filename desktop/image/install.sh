#!/usr/bin/env bash
# Installs the programs a Workstream desktop needs onto Debian 13, as root: a graphical session (XFCE on a
# TigerVNC display), the apps in desktop/apps.json and the usual developer tools. configure.sh, run after it,
# adds what is Workstream's own. Both places a desktop comes from run the two scripts in this order: the
# machine image (.github/workflows/desktop-image.yml) and the local container (desktop/Dockerfile).
#
# WORKSTREAM_SLIM=1 leaves out what the local container cannot use or does not need to prove the product
# works: Docker Engine, which cannot run inside a container, and the largest installs (LibreOffice, the .NET
# SDK, the C toolchain). The machine image is never slim.
set -euo pipefail
SLIM=${WORKSTREAM_SLIM:-}
export DEBIAN_FRONTEND=noninteractive
ARCH=$(dpkg --print-architecture)

apt-get update
apt-get install -y --no-install-recommends \
  ca-certificates curl gnupg git jq sudo locales less nano procps psmisc unzip zip xz-utils openssh-client iproute2 iputils-ping \
  python3 \
  dbus-x11 xauth x11-utils x11-xserver-utils xdg-utils at-spi2-core libnotify-bin \
  xfce4 xfce4-terminal xfce4-notifyd mousepad adwaita-icon-theme elementary-xfce-icon-theme greybird-gtk-theme librsvg2-common \
  tigervnc-standalone-server \
  fonts-dejavu fonts-liberation2 fonts-noto-color-emoji \
  xdotool scrot wmctrl xclip \
  chromium
apt-get clean
[ -n "$SLIM" ] || apt-get install -y --no-install-recommends \
  build-essential python3-pip python3-venv libicu-dev \
  libreoffice-writer libreoffice-calc libreoffice-impress libreoffice-gtk3

install -d -m 0755 /etc/apt/keyrings

# Node.js 22: the agent runs on it, and so does most web work.
curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
apt-get install -y --no-install-recommends nodejs
apt-get clean

# VS Code, from Microsoft's repository.
curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg
echo "deb [arch=$ARCH signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/code stable main" > /etc/apt/sources.list.d/vscode.list
echo "code code/add-microsoft-repo boolean false" | debconf-set-selections

# The GitHub CLI.
curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli.gpg
echo "deb [arch=$ARCH signed-by=/etc/apt/keyrings/githubcli.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list

# Docker Engine. It starts with the first `docker` command (image/configure.sh).
curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$ARCH signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian trixie stable" > /etc/apt/sources.list.d/docker.list

apt-get update
apt-get install -y --no-install-recommends code gh
apt-get clean
if [ -z "$SLIM" ]; then
  apt-get install -y --no-install-recommends docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  # The .NET SDK, from Microsoft's installer, which serves both processor types.
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
fi

apt-get clean
rm -rf /var/lib/apt/lists/*
echo "The desktop's programs are installed."
