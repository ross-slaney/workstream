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
# The kernel's swappiness as it was, kept while the machine hibernates.
was=/run/workstream-swappiness

# Whether Azure made this machine with hibernation on.
offered() { journalctl -k -b --no-pager 2>/dev/null | grep -F 'ACPI: PM: (supports' | grep -q ' S4'; }

prepare() {
  offered || exit 0
  local memory have offset uuid line
  memory=$(awk '/^MemTotal:/ { print $2 * 1024 }' /proc/meminfo)
  have=$(stat -c %s "$file" 2>/dev/null || echo 0)
  # A machine given a larger size since has more memory to write.
  if [ "$have" -lt "$memory" ]; then
    rm -f "$file"
    fallocate -l "$memory" "$file" || { rm -f "$file"; echo "No room on the disk for $memory bytes of memory. This desktop cannot hibernate."; exit 0; }
    chmod 600 "$file"
  fi
  # Written again at every boot: a boot that did not resume (a copy of a hibernated disk started as another
  # machine, image/resume-here.sh) leaves the other machine's memory in the file, marked as waiting to be read.
  mkswap -q -f "$file" > /dev/null
  # What the next boot is told, so it finds the memory and resumes: where the file begins on the disk, and
  # which machine this is, because only this one may wake up from it.
  offset=$(filefrag -v "$file" | awk '$1 == "0:" { sub(/\.\.$/, "", $4); print $4 }')
  uuid=$(findmnt -no UUID /)
  line="GRUB_CMDLINE_LINUX=\"\$GRUB_CMDLINE_LINUX resume=UUID=$uuid resume_offset=$offset workstream.machine=$(cat /sys/class/dmi/id/product_uuid)\""
  if [ "$(cat "$settings" 2>/dev/null)" != "$line" ]; then
    echo "$line" > "$settings"
    update-grub
  fi
}

now() {
  offered || exit 0
  grep -q "workstream.machine=$(cat /sys/class/dmi/id/product_uuid)" "$settings" 2>/dev/null || prepare
  swapon "$file" 2>/dev/null
  # What is written is what programs hold, with a quarter over, and not all the files the kernel keeps in
  # memory to save reading them again: a smaller image is written sooner and read back sooner. The kernel makes
  # room by letting go of those files first (swappiness 0): memory it pushed out to swap instead would have to
  # be read back a page at a time after the desktop is started, which measured a minute and a half of a
  # desktop that hardly answers.
  cat /proc/sys/vm/swappiness > "$was"
  echo 0 > /proc/sys/vm/swappiness
  awk '/^MemTotal:/ { total = $2 } /^MemAvailable:/ { available = $2 } END { printf "%d\n", (total - available) * 1024 * 1.25 + 268435456 }' /proc/meminfo > /sys/power/image_size
  # A desktop asked to hibernate a moment after it was started is still finishing the last time: asked again until it takes it.
  for _ in $(seq 30); do
    systemctl hibernate && return
    sleep 2
  done
  after
}

after() {
  swapoff "$file" 2>/dev/null
  [ -f "$was" ] && cat "$was" > /proc/sys/vm/swappiness && rm -f "$was"
  exit 0
}

case "${1:-}" in
  prepare) prepare ;;
  now) now ;;
  after) after ;;
  *) echo "hibernate.sh prepare|now|after" >&2; exit 2 ;;
esac
