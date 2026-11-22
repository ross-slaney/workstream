#!/usr/bin/env bash
# Makes a desktop able to hibernate, and hibernates it when Azure asks. As root.
#
#   hibernate.sh prepare   at boot (image/workstream-hibernate.service): the room memory is written to
#   hibernate.sh now       when Azure asks the machine to hibernate (the udev rule image/configure.sh installs)
#   hibernate.sh after     when the machine is back (the systemd-sleep hook image/configure.sh installs)
#
# Azure hibernates a machine by asking its operating system to write memory to its own disk and power off;
# starting the machine again reads it back, and every program is where it was (api/Desktops/DesktopHosts.cs,
# Hibernate). Azure offers it only to a machine made with hibernation on, and such a machine says so in its
# firmware (the S4 sleep state). On any other machine all three do nothing, so the image is the same for both.
#
# The room is a swap file as large as the machine's memory, which differs by size, so it is made here and not
# in the image. It is only used as swap while the machine hibernates: a desktop runs with no swap, as it
# always has, and the whole file is free for memory when it is needed.
set -u
file=/hibernate.swap
settings=/etc/default/grub.d/98-workstream-hibernate.cfg

# Whether Azure made this machine with hibernation on.
offered() { journalctl -k -b --no-pager 2>/dev/null | grep -F 'ACPI: PM: (supports' | grep -q ' S4'; }

prepare() {
  offered || exit 0
  local memory have offset uuid line
  memory=$(awk '/^MemTotal:/ { print $2 * 1024 }' /proc/meminfo)
  have=$(stat -c %s "$file" 2>/dev/null || echo 0)
  # A machine given a larger size since has more memory to write.
  if [ "$have" -lt "$memory" ]; then
    swapoff "$file" 2>/dev/null
    rm -f "$file"
    fallocate -l "$memory" "$file" || { rm -f "$file"; echo "No room on the disk for $memory bytes of memory. This desktop cannot hibernate."; exit 0; }
    chmod 600 "$file"
    mkswap -q "$file"
  fi
  # Where the file begins on the disk: what the next boot is told, so it finds the memory and resumes.
  offset=$(filefrag -v "$file" | awk '$1 == "0:" { sub(/\.\.$/, "", $4); print $4 }')
  uuid=$(findmnt -no UUID /)
  line="GRUB_CMDLINE_LINUX=\"\$GRUB_CMDLINE_LINUX resume=UUID=$uuid resume_offset=$offset\""
  if [ "$(cat "$settings" 2>/dev/null)" != "$line" ]; then
    echo "$line" > "$settings"
    update-grub
  fi
}

now() {
  offered || exit 0
  [ -f "$file" ] || prepare
  swapon "$file" 2>/dev/null
  systemctl hibernate
}

after() {
  swapoff "$file" 2>/dev/null
  exit 0
}

case "${1:-}" in
  prepare) prepare ;;
  now) now ;;
  after) after ;;
  *) echo "hibernate.sh prepare|now|after" >&2; exit 2 ;;
esac
