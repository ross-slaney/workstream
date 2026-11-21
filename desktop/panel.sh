#!/usr/bin/env bash
# Writes the desktop's panel: the applications menu, a launcher for each app the environment chose (and the
# agent's window, always), the open windows and a clock. Run before XFCE starts, so the panel is right from
# the first frame. The apps come from the API with the desktop's key; without an answer, the defaults in
# apps.json are used.
set -u
ROOT=/opt/workstream
[ -f /etc/workstream/desktop.env ] && . /etc/workstream/desktop.env
apps=""
if [ -n "${WORKSTREAM_API:-}" ] && [ -n "${WORKSTREAM_KEY:-}" ]; then
  for _ in $(seq 10); do
    apps=$(curl -fsS --max-time 5 -H "Authorization: Bearer $WORKSTREAM_KEY" "$WORKSTREAM_API/api/desktop/setup" 2>/dev/null | jq -r '.apps[]?' 2>/dev/null) && [ -n "$apps" ] && break
    sleep 2
  done
fi
[ -n "$apps" ] || apps=$(jq -r '.[] | select(.default) | .id' "$ROOT/apps.json")

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
