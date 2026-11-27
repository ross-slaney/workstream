#!/usr/bin/env bash
# Gives a desktop swap that is kept in memory, compressed, as root, at boot (image/workstream-swap.service).
#
# A project's whole stack on one desktop holds more memory than it uses at any moment: a database, a build's
# daemons, an Android emulator, an iPhone simulator's hundred programs, a browser, an editor. With no swap the
# kernel cannot set aside what nobody is using, so when memory runs short the only thing it can let go of is
# the programs' own files, which it then reads back from the disk at once. On a 32 GB desktop running a stack,
# a build and two phones this stopped the machine: the disk read 98 MB a second for ten minutes and wrote
# nothing, the processor was idle, and Azure still called 6.6 GB "available"
# (docs/architecture.md, "A desktop's memory").
#
# zram is a block device in memory that compresses what is written to it. The same work with it on set aside
# 6 GB in 1.1 GB of memory and went on. It is half the machine's memory at most, and costs nothing until it
# is used. A container has the machine's own kernel and swap, so this is for machines only.
set -u
[ -e /sys/block/zram0 ] && grep -q '^/dev/zram0 ' /proc/swaps && exit 0
modprobe zram || exit 0
half=$(awk '/^MemTotal:/ { printf "%dK", $2 / 2 }' /proc/meminfo)
device=$(zramctl --find --size "$half" --algorithm zstd) || exit 0
mkswap -q "$device" && swapon --priority 100 "$device"
