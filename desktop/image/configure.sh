#!/usr/bin/env bash
# Makes an installed Debian (image/install.sh) into a Workstream desktop, as root: the person who owns the
# session, the agent and its packages, and how the desktop's programs start. It expects this folder's parent
# at /opt/workstream. Quick to run again, so a change to the agent rebuilds only this step.
set -euo pipefail
ROOT=/opt/workstream

# The desktop's person. They own the session, may sudo without a password, and may use Docker.
id work >/dev/null 2>&1 || useradd --create-home --shell /bin/bash work
echo "work ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/work
chmod 0440 /etc/sudoers.d/work
getent group docker >/dev/null && usermod -aG docker work
# The emulator runs on the processor's virtualization, which /dev/kvm opens to this group.
getent group kvm >/dev/null && usermod -aG kvm work

# Chromium: no first-run questions, one profile shared by the person and the agent's browser tools (which
# attach on port 9222, open to this desktop only), and no sandbox of its own inside a container, which has
# none to give (said without the warning bar Chromium would otherwise show about it). A project's own server
# on https://localhost has a certificate nothing vouches for, whatever made it (mkcert, a framework's
# development certificate, a self-signed one): Chromium opens those without its "not private" page. Only
# this desktop itself is ever reached that way.
cat > /etc/chromium.d/workstream <<'EOF'
export CHROMIUM_FLAGS="$CHROMIUM_FLAGS --no-first-run --no-default-browser-check --password-store=basic --disable-features=Translate --allow-insecure-localhost --remote-debugging-port=9222 --user-data-dir=/home/work/.workstream/browser"
if [ -f /.dockerenv ]; then export CHROMIUM_FLAGS="$CHROMIUM_FLAGS --no-sandbox --test-type --disable-dev-shm-usage"; fi
EOF

# The accounts used on a desktop are a task's test accounts. Chromium does not offer to save their passwords
# or warn that one was found in a breach: in the first task that signed up in the browser, the "Save
# password?" bubble covered the page that was being handed over.
mkdir -p /etc/chromium/policies/managed
cat > /etc/chromium/policies/managed/workstream.json <<'EOF'
{
  "PasswordManagerEnabled": false,
  "PasswordLeakDetectionEnabled": false
}
EOF

# The agent's own window, opened from the panel.
cat > /usr/share/applications/workstream-agent.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=Agent
Comment=Talk to the agent working on this desktop
Exec=chromium --app=http://127.0.0.1:4141 --window-size=440,780
Icon=user-available
Terminal=false
Categories=Utility;
EOF

# The phones, opened from the panel when the environment has them (apps.json): each starts its phone and
# shows its window (agent/phone.mjs).
cat > /usr/share/applications/workstream-iphone.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=iPhone
Comment=The iPhone simulator on this desktop
Exec=phone ios start
Icon=phone
Terminal=false
Categories=Development;
EOF
cat > /usr/share/applications/workstream-android.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=Android phone
Comment=The Android phone on this desktop
Exec=phone android start
Icon=phone
Terminal=false
Categories=Development;
EOF
# `phone`, for the agent, for a terminal and for the launchers above.
cat > /usr/local/bin/phone <<'EOF'
#!/bin/sh
exec node /opt/workstream/agent/phone.mjs "$@"
EOF
chmod +x /usr/local/bin/phone

# `surfaces`, which puts the browser and the phones side by side (agent/surfaces.mjs).
cat > /usr/local/bin/surfaces <<'EOF'
#!/bin/sh
exec node /opt/workstream/agent/surfaces.mjs "$@"
EOF
chmod +x /usr/local/bin/surfaces

# The agent and its packages.
(cd "$ROOT/agent" && npm ci --omit=dev --no-audit --no-fund && npm cache clean --force >/dev/null 2>&1)
chmod +x "$ROOT/start.sh" "$ROOT/session.sh" "$ROOT/panel.sh" "$ROOT/agent/shims/gh" "$ROOT"/image/*.sh

# What the person's programs start with: no trust or telemetry questions in VS Code, the environment's
# variables in every shell, a plain window manager and a plain background, which are quick to stream, and
# the panel with the default apps, so a new desktop draws itself without asking anyone anything.
sudo -u work bash <<'EOF'
set -euo pipefail
mkdir -p ~/.workstream ~/work ~/.config/Code/User ~/.vscode ~/.config/xfce4/xfconf/xfce-perchannel-xml
cat > ~/.config/Code/User/settings.json <<'JSON'
{
  "security.workspace.trust.enabled": false,
  "telemetry.telemetryLevel": "off",
  "update.mode": "none",
  "extensions.autoUpdate": false,
  "extensions.ignoreRecommendations": true,
  "workbench.startupEditor": "none",
  "workbench.welcomePage.walkthroughs.openOnInstall": false,
  "chat.disableAIFeatures": true,
  "chat.commandCenter.enabled": false,
  "workbench.secondarySideBar.defaultVisibility": "hidden",
  "workbench.tips.enabled": false,
  "window.restoreWindows": "none",
  "git.openRepositoryInParentFolders": "never"
}
JSON
echo '{ "password-store": "basic" }' > ~/.vscode/argv.json
cat > ~/.config/xfce4/xfconf/xfce-perchannel-xml/xfwm4.xml <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<channel name="xfwm4" version="1.0">
  <property name="general" type="empty">
    <property name="use_compositing" type="bool" value="false"/>
    <property name="theme" type="string" value="Greybird"/>
  </property>
</channel>
XML
cat > ~/.config/xfce4/xfconf/xfce-perchannel-xml/xsettings.xml <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<channel name="xsettings" version="1.0">
  <property name="Net" type="empty">
    <property name="ThemeName" type="string" value="Greybird"/>
    <property name="IconThemeName" type="string" value="elementary-xfce"/>
  </property>
</channel>
XML
# The display is named VNC-0 (start.sh). No picture and one flat colour.
cat > ~/.config/xfce4/xfconf/xfce-perchannel-xml/xfce4-desktop.xml <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<channel name="xfce4-desktop" version="1.0">
  <property name="backdrop" type="empty">
    <property name="screen0" type="empty">
      <property name="monitorVNC-0" type="empty">
        <property name="workspace0" type="empty">
          <property name="image-style" type="int" value="0"/>
          <property name="color-style" type="int" value="0"/>
          <property name="rgba1" type="array">
            <value type="double" value="0.11"/>
            <value type="double" value="0.13"/>
            <value type="double" value="0.16"/>
            <value type="double" value="1"/>
          </property>
        </property>
      </property>
    </property>
  </property>
</channel>
XML
/opt/workstream/panel.sh
for file in ~/.bashrc ~/.profile; do
  grep -q workstream/env.sh "$file" 2>/dev/null || echo '[ -f "$HOME/.workstream/env.sh" ] && . "$HOME/.workstream/env.sh"' >> "$file"
done
git config --global init.defaultBranch main
EOF

# agent-sim, which drives the iPhone simulator (x86Sim running Apple's own iOS runtime), for the desktop's
# person: one release, checked against the hash written here. It is small. What it installs when an
# environment asks for the iPhone (`phone ios setup`) is not: x86Sim, the runtime from Apple and a phone.
# x86Sim runs on x86-64 only, and not inside a container.
AGENT_SIM_VERSION=v0.1.0
AGENT_SIM_SHA256=7417da971cc120250caca7ca611fe7540e923bb0a3a0ce595b99d01e5a7f7359
if [ "$(dpkg --print-architecture)" = amd64 ] && [ ! -f /.dockerenv ]; then
  archive=$(mktemp)
  curl -fsSL -o "$archive" "https://github.com/Developing-Gamer/agent-sim/releases/download/$AGENT_SIM_VERSION/agent-sim-x86_64-linux.tar.gz"
  echo "$AGENT_SIM_SHA256  $archive" | sha256sum -c --quiet
  chmod 0644 "$archive"
  sudo -u work -H bash -c "set -euo pipefail; mkdir -p ~/.local/share/agent-sim/src ~/.local/bin; tar -xzf '$archive' -C ~/.local/share/agent-sim/src --strip-components=1; ln -sf ~/.local/share/agent-sim/src/bin/agent-sim ~/.local/bin/agent-sim; ~/.local/bin/agent-sim --version"
  rm -f "$archive"
  # The phone runs as a service of the person's own systemd, which this keeps running without a login.
  loginctl enable-linger work 2>/dev/null || install -D /dev/null /var/lib/systemd/linger/work
fi

# A project's own tools, ready for the person: the ASP.NET Core development certificate (which an Aspire
# dashboard or any https://localhost serves), trusted by the browser and by .NET; and a phone for the Android
# emulator. The SDK is theirs, so Gradle can add what a project asks for.
[ -d /opt/android-sdk ] && chown -R work:work /opt/android-sdk
sudo -u work bash <<'EOF'
set -euo pipefail
. /etc/profile.d/workstream-tools.sh
if command -v dotnet >/dev/null; then
  dotnet dev-certs https --trust >/dev/null 2>&1 || dotnet dev-certs https >/dev/null
fi
if [ -d /opt/android-sdk/system-images ]; then
  /opt/workstream/image/phone.sh Phone
fi
EOF

# On a machine, the desktop starts at boot. A container has no service manager and runs start.sh itself.
# Docker does not start at boot: it would read its programs off a new machine's slow disk just when the
# desktop is reading its own. Its socket is there instead, and the first `docker` command starts it.
if [ ! -f /.dockerenv ] && command -v systemctl >/dev/null 2>&1; then
  install -m 0644 "$ROOT/image/workstream-desktop.service" /etc/systemd/system/workstream-desktop.service
  systemctl enable workstream-desktop.service
  # A machine's disk can be larger than the image's (api/Desktops/DesktopHosts.cs, DiskSizesGb). Nothing
  # provisions a desktop, so it grows its own file system to its disk when it first boots.
  chmod +x "$ROOT/image/grow-disk.sh"
  install -m 0644 "$ROOT/image/workstream-grow-disk.service" /etc/systemd/system/workstream-grow-disk.service
  systemctl enable workstream-grow-disk.service
  # A desktop Azure can hibernate (api/Desktops/DesktopHosts.cs, Hibernate) writes its memory to its own disk
  # when it is stopped, and is back as it was when it is started. Azure asks through the machine's Hyper-V
  # tools; the kernel passes the request on as an event, and nothing in Debian answers it. So: room for the
  # memory, made at boot, and the rule that answers the request (run as a unit of its own, because a rule's
  # program may not turn swap on and is ended after a while). On a machine Azure was not asked to make
  # hibernate, both do nothing (image/hibernate.sh).
  chmod +x "$ROOT/image/hibernate.sh"
  # Only the machine that hibernated wakes up from its disk; a copy started as another machine boots afresh.
  install -m 0755 "$ROOT/image/resume-here.sh" /etc/initramfs-tools/scripts/init-premount/workstream-resume-here
  update-initramfs -u
  install -m 0644 "$ROOT/image/workstream-hibernate.service" /etc/systemd/system/workstream-hibernate.service
  systemctl enable workstream-hibernate.service
  cat > /etc/udev/rules.d/90-workstream-hibernate.rules <<'RULE'
SUBSYSTEM=="vmbus", ACTION=="change", DRIVER=="hv_utils", ENV{EVENT}=="hibernate", RUN+="/usr/bin/systemd-run --no-block /opt/workstream/image/hibernate.sh now"
RULE
  # A project's stack watches many files (Metro, dotnet watch, VS Code) and keeps many open. The last line is
  # for host.docker.internal, below.
  cat > /etc/sysctl.d/90-workstream.conf <<'CONF'
fs.inotify.max_user_watches = 1048576
fs.inotify.max_user_instances = 8192
vm.max_map_count = 262144
net.ipv4.conf.all.route_localnet = 1
CONF
  if [ -d /etc/docker ]; then
    # host.docker.internal. On a Mac, Docker Desktop gives every container that name for the computer it
    # runs on, and projects' scripts and settings use it. Docker Engine on Linux has no such name, and no
    # setting that adds one to every container, so a desktop gives it itself, with no change to a project:
    #
    #   - The name is one address, 172.16.255.254, in /etc/hosts. It is outside the networks Docker hands out
    #     (172.17 to 172.31, and 192.168).
    #   - Containers ask the desktop for names: Docker tells each one to ask at its bridge's address ("dns"),
    #     where the desktop's own resolver listens as well and answers from /etc/hosts, then asks onward as
    #     before. That covers `docker run`, a compose project's own network and the steps of a build.
    #   - `--add-host host.docker.internal:host-gateway`, which projects written for Linux already pass, means
    #     the same address ("host-gateway-ip").
    #   - Whatever is sent to that address is delivered to the desktop's loopback address
    #     (image/host-docker-internal.nft), so a server listening only on localhost answers too, as it does
    #     through Docker Desktop.
    #
    # Checked when the image is baked (image/check-host-name.sh). Containers' logs are kept small, on a disk
    # they share with the work.
    cat > /etc/docker/daemon.json <<'JSON'
{
  "log-driver": "local",
  "log-opts": { "max-size": "20m", "max-file": "3" },
  "bip": "172.17.0.1/16",
  "dns": ["172.17.0.1"],
  "host-gateway-ip": "172.16.255.254"
}
JSON
    grep -q "host.docker.internal" /etc/hosts || echo "172.16.255.254 host.docker.internal" >> /etc/hosts
    install -d /etc/systemd/resolved.conf.d
    printf '[Resolve]\nDNSStubListenerExtra=172.17.0.1\n' > /etc/systemd/resolved.conf.d/workstream.conf
    install -m 0644 "$ROOT/image/workstream-host-name.service" /etc/systemd/system/workstream-host-name.service
    systemctl enable workstream-host-name.service
  fi
  if systemctl cat docker.socket >/dev/null 2>&1; then
    systemctl disable docker.service containerd.service
    systemctl enable docker.socket
  fi
fi

echo "The desktop is configured."
