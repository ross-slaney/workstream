#!/usr/bin/env bash
# Starts a Workstream desktop: sets up who and where, then hands over to the session (session.sh) inside a
# message bus of its own. Runs as the desktop's person, `work`, from the machine's service
# (image/workstream-desktop.service) or as the container's command. Nothing here waits for the network or
# for the API.
set -u
export HOME=/home/work USER=work LOGNAME=work SHELL=/bin/bash LANG=C.UTF-8
export DISPLAY=:1
# On a machine the service gives the session a folder in memory; a container has /tmp.
export XDG_RUNTIME_DIR="${RUNTIME_DIRECTORY:-/tmp/runtime-work}"
mkdir -p "$XDG_RUNTIME_DIR" "$HOME/.workstream"
chmod 700 "$XDG_RUNTIME_DIR"
# A container cannot give programs a sandbox of their own; VS Code is told not to ask for one.
if [ -f /.dockerenv ]; then export ELECTRON_DISABLE_SANDBOX=1; fi
# Where the developer tools are (.NET's global tools, Java and the Android SDK), for every program started here.
# shellcheck disable=SC1091
[ -f /etc/profile.d/workstream-tools.sh ] && . /etc/profile.d/workstream-tools.sh
cd "$HOME" || exit 1
exec dbus-run-session -- /opt/workstream/session.sh
