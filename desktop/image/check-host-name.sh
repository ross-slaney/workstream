#!/usr/bin/env bash
# Checks, as root, that host.docker.internal is this machine, for a container and for the machine itself,
# with a server that listens only on localhost (configure.sh, "host.docker.internal"). Run when the image is
# baked (bake.sh).
#
# It pulls no image. A container is a network namespace joined to Docker's bridge that asks the bridge's
# address for names (daemon.json's "dns"), and that is what this makes by hand: it asks for the name there,
# then fetches a file from the address it was given.
set -euo pipefail
name=host.docker.internal
port=18001
served=$(mktemp -d)
echo served > "$served/marker"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$served" > /dev/null 2>&1 &
server=$!
# The first docker command starts Docker, which makes its bridge.
docker info > /dev/null
ip netns add wscheck
finish() {
  kill "$server" 2> /dev/null || true
  ip netns del wscheck 2> /dev/null || true
  ip link del wscheck0 2> /dev/null || true
  rm -rf "$served"
}
trap finish EXIT
ip link add wscheck0 type veth peer name wscheck1
ip link set wscheck0 master docker0 up
ip link set wscheck1 netns wscheck
ip -n wscheck addr add 172.17.255.250/16 dev wscheck1
ip -n wscheck link set lo up
ip -n wscheck link set wscheck1 up
ip -n wscheck route add default via 172.17.0.1
for _ in $(seq 20); do curl -fsS -m 2 "http://127.0.0.1:$port/marker" > /dev/null 2>&1 && break; sleep 0.5; done

# From the machine itself.
[ "$(curl -fsS -m 5 "http://$name:$port/marker")" = served ] || { echo "$name does not reach this machine from itself."; exit 1; }

# From a container's place: the name, asked of the bridge's address, then the server at that address.
address=$(ip netns exec wscheck python3 - "$name" <<'PY'
import socket, sys
question = b"\x12\x34\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00" + b"".join(bytes([len(part)]) + part.encode() for part in sys.argv[1].split(".")) + b"\x00\x00\x01\x00\x01"
asking = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
asking.settimeout(5)
asking.sendto(question, ("172.17.0.1", 53))
answer = asking.recv(512)
# One address in answer: the last four bytes. No answer at all leaves the count at zero.
print(socket.inet_ntoa(answer[-4:]) if answer[6:8] != b"\x00\x00" else "")
PY
)
[ -n "$address" ] || { echo "A container is not told where $name is."; exit 1; }
[ "$(ip netns exec wscheck curl -fsS -m 5 "http://$address:$port/marker")" = served ] || { echo "$name ($address) does not reach this machine from a container."; exit 1; }
echo "$name is this machine ($address), for a container and for itself."
