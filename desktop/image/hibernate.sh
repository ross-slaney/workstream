#!/usr/bin/env bash
# Makes a desktop able to hibernate, and hibernates it when Azure asks. As root.
#
#   hibernate.sh prepare   at boot (image/workstream-hibernate.service): the room memory is written to
#   hibernate.sh now       when Azure asks the machine to hibernate (the udev rule image/configure.sh installs)
#
# Azure hibernates a machine by asking its operating system to write memory to its own disk and power off;
# starting the machine again reads it back, and every program is where it was (api/Desktops/DesktopHosts.cs,
# Hibernate). Azure offers it only to a machine made with hibernation on, and such a machine says so in its
# firmware (the S4 sleep state). On any other machine both do nothing, so the image is the same for all.
#
# The room is a swap file as large as the machine's memory, which differs by size, so it is made here and not
# in the image. It is only used as swap while the machine hibernates: a desktop runs with no swap, as it
# always has, and the whole file is free for memory when it is needed.
#
# The kernel is told what to do directly (/sys/power), not through `systemctl hibernate`. systemd refuses to
# hibernate when the boot settings this boot started with name another place on the disk than the swap now in
# use, which is the case on the boot in which the file is made or made larger; and for a minute or so after a
# resume it refuses because it counts the last hibernation as still going. Azure waits ten minutes for a
# machine that does not hibernate and then leaves it running.
set -u
file=/hibernate.swap
settings=/etc/default/grub.d/98-workstream-hibernate.cfg

# Whether Azure made this machine with hibernation on.
offered() { journalctl -k -b --no-pager 2>/dev/null | grep -F 'ACPI: PM: (supports' | grep -q ' S4'; }

# Where the file begins on the disk, in the file system's blocks: where the kernel writes memory, and reads it back.
offset() { filefrag -v "$file" | awk '$1 == "0:" { sub(/\.\.$/, "", $4); print $4 }'; }

prepare() {
  offered || exit 0
  local memory have line
  # Whole gigabytes, rounded up: what the kernel reports differs by a little from one boot to the next.
  memory=$(awk '/^MemTotal:/ { printf "%d\n", (int($2 / 1048576) + 1) * 1073741824 }' /proc/meminfo)
  have=$(stat -c %s "$file" 2>/dev/null || echo 0)
  # A machine given a larger size since has more memory to write.
  if [ "$have" -lt "$memory" ]; then
    rm -f "$file"
    fallocate -l "$memory" "$file" || { rm -f "$file"; echo "No room on the disk for $memory bytes of memory. This desktop cannot hibernate."; exit 0; }
    chmod 600 "$file"
  fi
  # Written again each time: a boot that did not resume (a copy of a hibernated disk started as another
  # machine, image/resume-here.sh) leaves the other machine's memory in the file, marked as waiting to be read.
  mkswap -q -f "$file" > /dev/null
  # What the next boot is told, so it finds the memory and resumes: where the file begins, and which machine
  # this is, because only this one may wake up from it.
  line="GRUB_CMDLINE_LINUX=\"\$GRUB_CMDLINE_LINUX resume=UUID=$(findmnt -no UUID -T "$file") resume_offset=$(offset) workstream.machine=$(cat /sys/class/dmi/id/product_uuid)\""
  if [ "$(cat "$settings" 2>/dev/null)" != "$line" ]; then
    echo "$line" > "$settings"
    update-grub
  fi
}

now() {
  offered || exit 0
  # Whatever state an earlier try left: the room as it should be, and the next boot told where it is.
  swapoff "$file" 2> /dev/null
  prepare
  [ -f "$file" ] || exit 0
  swapon "$file" || exit 0
  # What is written is what programs hold, with a quarter over, and not all the files the kernel keeps in
  # memory to save reading them again: a smaller image is written sooner and read back sooner. The kernel makes
  # room by letting go of those files first (swappiness 0): memory it pushed out to swap instead would have to
  # be read back a page at a time after the desktop is started, which measured a minute and a half of a
  # desktop that hardly answers.
  local swappiness
  swappiness=$(cat /proc/sys/vm/swappiness)
  echo 0 > /proc/sys/vm/swappiness
  awk '/^MemTotal:/ { total = $2 } /^MemAvailable:/ { available = $2 } END { printf "%d\n", (total - available) * 1024 * 1.25 + 268435456 }' /proc/meminfo > /sys/power/image_size
  offset > /sys/power/resume_offset
  findmnt -no MAJ:MIN -T "$file" > /sys/power/resume
  echo platform > /sys/power/disk
  # This line returns when the machine is back, or at once when the kernel could not do it; then it is asked again.
  for _ in 1 2 3; do
    echo disk > /sys/power/state && break
    sleep 5
  done
  echo "$swappiness" > /proc/sys/vm/swappiness
  swapoff "$file"
}

case "${1:-}" in
  prepare) prepare ;;
  now) now ;;
  *) echo "hibernate.sh prepare|now" >&2; exit 2 ;;
esac
