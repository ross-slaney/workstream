#!/bin/sh
# In the machine's early boot (initramfs-tools, init-premount), before it looks for a hibernated machine to
# resume: only the machine that hibernated is resumed.
#
# A hibernated desktop's memory is on its disk, and the next boot of that disk reads it back (hibernate.sh).
# But a disk can be started as another machine: an archive of a hibernated desktop is a copy of its disk, and
# bringing it back makes a new machine from the copy, with a new network card and its own key in its user
# data. That machine must start as any machine does and not wake up as the old one. So the boot settings name
# the machine they were written for (hibernate.sh prepare), and on any other machine the resume is called off
# here; the memory on the disk is then written over the next time the room is prepared.
PREREQ=""
prereqs() { echo "$PREREQ"; }
case $1 in
prereqs)
	prereqs
	exit 0
	;;
esac

wrote=""
# shellcheck disable=SC2013
for word in $(cat /proc/cmdline); do
	case $word in
	workstream.machine=*) wrote="${word#workstream.machine=}" ;;
	esac
done
[ -n "$wrote" ] || exit 0
[ "$wrote" = "$(cat /sys/class/dmi/id/product_uuid 2>/dev/null)" ] && exit 0
echo "This disk was hibernated as another machine ($wrote). Starting without its memory."
# What the scripts after this one are given (initramfs-tools reads this file after each script).
echo "resume=" >> /conf/param.conf
exit 0
