#!/usr/bin/env bash
# Starts a Workstream desktop: the display, then the session (session.sh). Runs as the desktop's person,
# `work`, from the machine's service (image/workstream-desktop.service) or as the container's command.
set -u
export HOME=/home/work USER=work LOGNAME=work SHELL=/bin/bash LANG=C.UTF-8
export DISPLAY=:1
export XDG_RUNTIME_DIR=/tmp/runtime-work
mkdir -p "$XDG_RUNTIME_DIR" "$HOME/.workstream"
chmod 700 "$XDG_RUNTIME_DIR"
# A container cannot give programs a sandbox of their own; VS Code is told not to ask for one.
if [ -f /.dockerenv ]; then export ELECTRON_DISABLE_SANDBOX=1; fi
cd "$HOME" || exit 1

# The display: an X server that is also the VNC server the API's relay connects to. It asks for no password
# because the network admits only the API (infra/desktops.bicep), and the API authorizes every viewer.
# Several people can watch at once. The screen keeps one size whoever is watching, because the agent works on
# it whether or not anyone is; the web app fits it to the space it has. Four by three, because that space is
# nearly square: the conversation takes the side of every screen.
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1
Xtigervnc :1 -geometry "${WORKSTREAM_SCREEN:-1280x960}" -depth 24 -rfbport 5900 -localhost=0 \
  -SecurityTypes None -AlwaysShared -desktop Workstream >"$HOME/.workstream/display.log" 2>&1 &
for _ in $(seq 100); do [ -S /tmp/.X11-unix/X1 ] && break; sleep 0.1; done
xset s off -dpms 2>/dev/null || true

exec dbus-run-session -- /opt/workstream/session.sh
