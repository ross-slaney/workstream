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
# none to give (said without the warning bar Chromium would otherwise show about it).
cat > /etc/chromium.d/workstream <<'EOF'
export CHROMIUM_FLAGS="$CHROMIUM_FLAGS --no-first-run --no-default-browser-check --password-store=basic --disable-features=Translate --remote-debugging-port=9222 --user-data-dir=/home/work/.workstream/browser"
if [ -f /.dockerenv ]; then export CHROMIUM_FLAGS="$CHROMIUM_FLAGS --no-sandbox --test-type --disable-dev-shm-usage"; fi
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

# The agent and its packages.
(cd "$ROOT/agent" && npm ci --omit=dev --no-audit --no-fund && npm cache clean --force >/dev/null 2>&1)
chmod +x "$ROOT/start.sh" "$ROOT/session.sh" "$ROOT/panel.sh" "$ROOT/agent/shims/gh"

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
  echo no | avdmanager create avd --force -n Phone -k "system-images;android-36;google_apis;x86_64" -d pixel_7 >/dev/null
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
  # memory, made at boot; the rule that answers the request (run as a unit of its own, because a rule's
  # program may not turn swap on); and the swap turned off again once the machine is back. On a machine Azure
  # was not asked to make hibernate, all three do nothing (image/hibernate.sh).
  chmod +x "$ROOT/image/hibernate.sh"
  # Only the machine that hibernated wakes up from its disk; a copy started as another machine boots afresh.
  install -m 0755 "$ROOT/image/resume-here.sh" /etc/initramfs-tools/scripts/init-premount/workstream-resume-here
  update-initramfs -u
  install -m 0644 "$ROOT/image/workstream-hibernate.service" /etc/systemd/system/workstream-hibernate.service
  systemctl enable workstream-hibernate.service
  cat > /etc/udev/rules.d/90-workstream-hibernate.rules <<'RULE'
SUBSYSTEM=="vmbus", ACTION=="change", DRIVER=="hv_utils", ENV{EVENT}=="hibernate", RUN+="/usr/bin/systemd-run --no-block /opt/workstream/image/hibernate.sh now"
RULE
  printf '#!/bin/sh\n[ "$1" = post ] && exec /opt/workstream/image/hibernate.sh after\nexit 0\n' > /usr/lib/systemd/system-sleep/workstream-hibernate
  chmod +x /usr/lib/systemd/system-sleep/workstream-hibernate
  # A project's stack watches many files (Metro, dotnet watch, VS Code) and keeps many open.
  cat > /etc/sysctl.d/90-workstream.conf <<'CONF'
fs.inotify.max_user_watches = 1048576
fs.inotify.max_user_instances = 8192
vm.max_map_count = 262144
CONF
  # Containers' logs are kept small, on a disk they share with the work.
  if [ -d /etc/docker ]; then
    echo '{ "log-driver": "local", "log-opts": { "max-size": "20m", "max-file": "3" } }' > /etc/docker/daemon.json
  fi
  if systemctl cat docker.socket >/dev/null 2>&1; then
    systemctl disable docker.service containerd.service
    systemctl enable docker.socket
  fi
fi

echo "The desktop is configured."
