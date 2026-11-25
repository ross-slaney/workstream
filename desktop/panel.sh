#!/usr/bin/env bash
# Writes the desktop's panel: the applications menu, a launcher for each app given (and the agent's window,
# always), the open windows and a clock.
#
#   panel.sh [app …]      the ids in apps.json; none means the default apps
#
# The image is built with the default panel (image/configure.sh), so a new desktop draws it without asking
# anyone. The agent runs this again when the task's environment chose other apps (agent/agent.mjs): then the
# panel is already on screen, and it is started again to show the change.
set -u
ROOT=/opt/workstream
apps="$*"
[ -n "$apps" ] || apps=$(jq -r '.[] | select(.default) | .id' "$ROOT/apps.json")

# In a session that is up, the panel is stopped before anything is written: a running panel writes its own
# launchers back as it goes, over the ones written here.
session=no
if [ -n "${DISPLAY:-}" ] && pgrep -x xfwm4 >/dev/null 2>&1; then
  session=yes
  xfce4-panel --quit >/dev/null 2>&1 || true
  for _ in $(seq 50); do pgrep -x xfce4-panel >/dev/null 2>&1 || break; sleep 0.1; done
  pkill -x xfce4-panel 2>/dev/null || true
fi

panel="$HOME/.config/xfce4/panel"
config="$HOME/.config/xfce4/xfconf/xfce-perchannel-xml/xfce4-panel.xml"
mkdir -p "$panel" "$(dirname "$config")"
rm -rf "$panel"/launcher-*

launchers="workstream-agent.desktop"
for app in $apps; do
  file=$(jq -r --arg id "$app" '.[] | select(.id == $id) | .launcher' "$ROOT/apps.json")
  [ -n "$file" ] && [ -f "/usr/share/applications/$file" ] && launchers="$launchers $file"
done

ids=""
plugins=""
id=2
for file in $launchers; do
  mkdir -p "$panel/launcher-$id"
  cp "/usr/share/applications/$file" "$panel/launcher-$id/$file"
  ids="$ids<value type=\"int\" value=\"$id\"/>"
  plugins="$plugins<property name=\"plugin-$id\" type=\"string\" value=\"launcher\"><property name=\"items\" type=\"array\"><value type=\"string\" value=\"$file\"/></property></property>"
  id=$((id + 1))
done

cat > "$config" <<XML
<?xml version="1.0" encoding="UTF-8"?>
<channel name="xfce4-panel" version="1.0">
  <property name="configver" type="int" value="2"/>
  <property name="panels" type="array">
    <value type="int" value="1"/>
    <property name="panel-1" type="empty">
      <property name="position" type="string" value="p=8;x=0;y=0"/>
      <property name="length" type="uint" value="100"/>
      <property name="position-locked" type="bool" value="true"/>
      <property name="icon-size" type="uint" value="28"/>
      <property name="size" type="uint" value="44"/>
      <property name="plugin-ids" type="array">
        <value type="int" value="1"/>${ids}<value type="int" value="90"/><value type="int" value="91"/><value type="int" value="92"/>
      </property>
    </property>
  </property>
  <property name="plugins" type="empty">
    <property name="plugin-1" type="string" value="applicationsmenu">
      <property name="show-button-title" type="bool" value="false"/>
    </property>
    ${plugins}
    <property name="plugin-90" type="string" value="tasklist"/>
    <property name="plugin-91" type="string" value="separator">
      <property name="expand" type="bool" value="true"/>
      <property name="style" type="uint" value="0"/>
    </property>
    <property name="plugin-92" type="string" value="clock"/>
  </property>
</channel>
XML

# The settings service read this file when it started, so it is started again, and then the panel. The panel
# is looked for afterwards and started once more if it is not there: asked to restart itself while the
# settings service was going away, it sometimes did not come back, and the desktop was left without one.
if [ "$session" = yes ]; then
  pkill -x xfconfd 2>/dev/null || true
  for _ in 1 2 3 4 5; do
    pgrep -x xfce4-panel >/dev/null 2>&1 && break
    (setsid xfce4-panel --disable-wm-check >/dev/null 2>&1 &)
    sleep 2
  done
fi
