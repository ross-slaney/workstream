#!/usr/bin/env bash
# The desktop's session, inside its message bus. Three things start at once, because a new machine's disk is
# slow the first time anything is read and each of them is waited for by a person: the agent, which tells the
# API the desktop is up and is started again if it ever stops; the display; and XFCE on it. The agent waits
# for the windows before it opens any (agent/agent.mjs). Ends when XFCE does, and the service or container
# starts over.
set -u
(
  while true; do
    node /opt/workstream/agent/agent.mjs >>"$HOME/.workstream/agent.log" 2>&1
    sleep 3
  done
) &

# The display: an X server that is also the VNC server the API's relay connects to. It asks for no password
# because the network admits only the API (infra/foundation.bicep), and the API authorizes every viewer.
# Several people can watch at once. The screen keeps one size whoever is watching, because the agent works on
# it whether or not anyone is; the web app fits it to the space it has. Four by three, because that space is
# nearly square: the conversation takes the side of every screen.
#
# Every connection comes from the one address the API has, so the server's habit of shutting out an address
# that connects a few times without signing in is turned off: it would shut out everyone watching, and the
# API connects and leaves once just to learn that the display is up.
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1
Xtigervnc :1 -geometry "${WORKSTREAM_SCREEN:-1280x960}" -depth 24 -rfbport 5900 -localhost=0 \
  -SecurityTypes None -AlwaysShared -BlacklistThreshold=1000000 -BlacklistTimeout=0 -desktop Workstream >"$HOME/.workstream/display.log" 2>&1 &
for _ in $(seq 400); do [ -S /tmp/.X11-unix/X1 ] && break; sleep 0.025; done
# The first picture anyone sees is the desktop's own background colour (image/configure.sh), not black.
xsetroot -solid '#1c2129' 2>/dev/null || true
xset s off -dpms 2>/dev/null || true

startxfce4 >"$HOME/.workstream/xfce.log" 2>&1
