#!/usr/bin/env bash
# Makes the machine this runs on into the desktops' image: a disk that any number of machines start from, as
# it is. Azure calls such an image specialized. Nothing provisions a machine made from it: it boots straight
# into the desktop (workstream-desktop.service) and finds out what it is for from its user data
# (agent/told.mjs). So the disk carries no machine's identity, no account but the desktop's own, and nothing
# that waits for an answer at boot. Runs once, as root, as bake.sh's last step.
set -euo pipefail

# cloud-init set this builder up. A desktop has no use for it, and it is the slow part of a first boot: off,
# and its traces gone, with the network settings it wrote for this builder's own network card and the
# address this builder leased, which a new machine would otherwise ask for again and wait to be refused.
cloud-init clean --logs --seed
touch /etc/cloud/cloud-init.disabled
rm -f /etc/netplan/50-cloud-init.yaml /var/lib/dhcpcd/*.lease

# The network, whichever card the machine is given: Azure's is the one hv_netvsc drives. The machine does
# not announce a name, because every desktop has the same one.
cat > /etc/systemd/network/10-azure.network <<'EOF'
[Match]
Driver=hv_netvsc

[Network]
DHCP=ipv4
LinkLocalAddressing=ipv6

[DHCPv4]
RouteMetric=100
UseMTU=true
SendHostname=false
EOF

# No SSH: nothing reaches a desktop but the API's relay, on the display's port (infra/foundation.bicep).
systemctl disable ssh.service
rm -f /etc/ssh/ssh_host_*

# One name for every desktop, and an identity each machine makes for itself the first time it starts.
echo desktop > /etc/hostname
sed -i '/^127\.0\.1\.1[[:space:]]/d' /etc/hosts
echo "127.0.1.1 desktop" >> /etc/hosts
: > /etc/machine-id

# None of the account this was built with, and none of its history. Azure keeps a run command's script, with
# its arguments, beside its output: bake.sh was given a token.
userdel --remove --force builder 2>/dev/null || true
rm -f /root/.bash_history /home/work/.bash_history /var/lib/waagent/run-command/download/*/script.sh
rm -rf /var/log/journal/*
apt-get clean
