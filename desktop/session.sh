#!/usr/bin/env bash
# The desktop's session, inside its message bus: the panel for the chosen apps, XFCE, then the agent, which
# is started again if it ever stops. Ends when XFCE does, and the service or container starts over.
set -u
/opt/workstream/panel.sh || true
startxfce4 >"$HOME/.workstream/xfce.log" 2>&1 &
xfce=$!
for _ in $(seq 150); do wmctrl -m >/dev/null 2>&1 && break; sleep 0.2; done
# A plain background streams faster than a picture.
for property in $(xfconf-query -c xfce4-desktop -l 2>/dev/null | grep -E '/(image-style|color-style)$'); do
  xfconf-query -c xfce4-desktop -p "$property" -s 0 2>/dev/null || true
done
(
  while true; do
    node /opt/workstream/agent/agent.mjs >>"$HOME/.workstream/agent.log" 2>&1
    sleep 3
  done
) &
wait "$xfce"
