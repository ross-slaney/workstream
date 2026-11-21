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
install -d -m 0750 -o root -g work /etc/workstream

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
chmod +x "$ROOT/start.sh" "$ROOT/session.sh" "$ROOT/panel.sh"

# What the person's programs start with: no trust or telemetry questions in VS Code, the environment's
# variables in every shell, and a plain window manager that is quick to stream.
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
for file in ~/.bashrc ~/.profile; do
  echo '[ -f "$HOME/.workstream/env.sh" ] && . "$HOME/.workstream/env.sh"' >> "$file"
done
git config --global init.defaultBranch main
EOF

# On a machine, the desktop starts at boot. A container has no service manager and runs start.sh itself.
if [ ! -f /.dockerenv ] && command -v systemctl >/dev/null 2>&1; then
  install -m 0644 "$ROOT/image/workstream-desktop.service" /etc/systemd/system/workstream-desktop.service
  systemctl enable workstream-desktop.service docker.service
fi

echo "The desktop is configured."
