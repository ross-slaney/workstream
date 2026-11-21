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
  ca-certificates curl gnupg git git-lfs jq sudo locales less nano procps psmisc lsof rsync unzip zip xz-utils bzip2 openssh-client iproute2 iputils-ping \
  cloud-guest-utils libnss3-tools \
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
  # The .NET SDK, from Microsoft's installer, which serves both processor types. Aspire (9 and later) is a
  # NuGet package of the project that uses it, so there is no workload to install.
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
  # Where an app or a global tool finds the runtime.
  install -d /etc/dotnet && echo /usr/share/dotnet > /etc/dotnet/install_location
  # sqlcmd, for SQL Server in a container (Microsoft's go-sqlcmd; it needs no ODBC driver).
  curl -fsSL "https://github.com/microsoft/go-sqlcmd/releases/download/v1.11.0/sqlcmd-linux-$ARCH.tar.bz2" | tar -xj -C /usr/local/bin sqlcmd
  # What Playwright's browsers need to run, for a project whose tests drive Chromium, Firefox or WebKit. The
  # browsers themselves are the project's own version (`npx playwright install`), downloaded by the project.
  npx -y playwright@1.65.1 install-deps

  # Android: the SDK, for a project with an Android app, and its emulator. The emulator runs on an Intel
  # machine; on AMD's (every Dasv7 size) /dev/kvm opens but its virtual machine stops as it starts
  # (docs/architecture.md, "A project's whole stack on a desktop").
  # Java 17, which React Native and the Android Gradle plugin build with, is Adoptium's: Debian 13 has none.
  # The SDK belongs to the desktop's person (image/configure.sh), so Gradle adds the platforms a project asks for.
  if [ "$ARCH" = amd64 ]; then
    curl -fsSL https://packages.adoptium.net/artifactory/api/gpg/key/public | gpg --dearmor -o /etc/apt/keyrings/adoptium.gpg
    echo "deb [arch=$ARCH signed-by=/etc/apt/keyrings/adoptium.gpg] https://packages.adoptium.net/artifactory/deb trixie main" > /etc/apt/sources.list.d/adoptium.list
    apt-get update
    apt-get install -y --no-install-recommends temurin-17-jdk libpulse0 libxkbfile1 libgl1
    export JAVA_HOME=/usr/lib/jvm/temurin-17-jdk-amd64 ANDROID_HOME=/opt/android-sdk
    install -d "$ANDROID_HOME/cmdline-tools"
    tools=$(mktemp -d)
    curl -fsSL -o "$tools/tools.zip" https://dl.google.com/android/repository/commandlinetools-linux-16111833_latest.zip
    unzip -q "$tools/tools.zip" -d "$tools" && mv "$tools/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest" && rm -rf "$tools"
    { yes || true; } | "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" --licenses > /dev/null
    "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" --install platform-tools emulator "platforms;android-36" "build-tools;36.0.0" \
      "system-images;android-36;google_apis;x86_64" > /dev/null
  fi
fi

# Where the tools are, for every program on the desktop: start.sh reads this, and so does every login shell.
cat > /etc/profile.d/workstream-tools.sh <<'EOF'
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ROOT=/usr/share/dotnet
export PATH="$HOME/.dotnet/tools:$PATH"
# The ASP.NET Core development certificate is trusted by .NET's own clients too (image/configure.sh).
[ -d "$HOME/.aspnet/dev-certs/trust" ] && export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/usr/lib/ssl/certs"
if [ -d /opt/android-sdk ]; then
  export JAVA_HOME=/usr/lib/jvm/temurin-17-jdk-amd64 ANDROID_HOME=/opt/android-sdk ANDROID_SDK_ROOT=/opt/android-sdk
  export PATH="$PATH:/opt/android-sdk/cmdline-tools/latest/bin:/opt/android-sdk/platform-tools:/opt/android-sdk/emulator"
fi
EOF

apt-get clean
rm -rf /var/lib/apt/lists/*
echo "The desktop's programs are installed."
