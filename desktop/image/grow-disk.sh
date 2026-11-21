#!/usr/bin/env bash
# Grows the root partition and its file system to the whole disk, as root, at boot
# (image/workstream-grow-disk.service). A desktop's disk can be larger than the image it was made from
# (api/Desktops/DesktopHosts.cs, DiskSizesGb), and nothing provisions a desktop to do it. The root partition
# is the last one on the disk in Debian's cloud images. Once it fills the disk, this changes nothing.
set -u
source=$(readlink -f "$(findmnt -no SOURCE /)")
name=$(basename "$source")
disk=/dev/$(lsblk -no PKNAME "$source")
partition=$(cat "/sys/class/block/$name/partition")
# growpart says NOCHANGE, and fails, when the partition already fills the disk.
if growpart "$disk" "$partition"; then
  resize2fs "$source"
fi
exit 0
