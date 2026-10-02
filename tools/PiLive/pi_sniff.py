"""Runs ON the test server (a Linux relay box), as root: kernel-timestamp every UDP packet to or from the relay port and
one client address, in and out of the network card. Read-only: a packet socket only listens, and nothing is changed.
Usage: sudo python3 pi_sniff.py <client ip> <seconds> <out file> [relay port] [interface]

Each line: kernel time, in|out (as the server sees it), RemSound packet type, sequence, payload length, stream id."""
import socket, struct, sys, time

PEER = socket.inet_aton(sys.argv[1])
SECONDS = float(sys.argv[2])
OUT = sys.argv[3]
PORT = int(sys.argv[4]) if len(sys.argv) > 4 else 47830
IFACE = sys.argv[5] if len(sys.argv) > 5 else "eth0"
SO_TIMESTAMPNS = 35
ETH_P_ALL = 0x0003

s = socket.socket(socket.AF_PACKET, socket.SOCK_RAW, socket.htons(ETH_P_ALL))
s.bind((IFACE, 0))
s.setsockopt(socket.SOL_SOCKET, SO_TIMESTAMPNS, 1)
s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
s.settimeout(0.5)
rows = []
end = time.monotonic() + SECONDS
while time.monotonic() < end:
    try:
        data, anc, _flags, _addr = s.recvmsg(2048, socket.CMSG_SPACE(16))
    except socket.timeout:
        continue
    if len(data) < 14 + 20 + 8 or data[12:14] != b"\x08\x00":
        continue
    ip = data[14:]
    if ip[9] != 17:
        continue
    src, dst = ip[12:16], ip[16:20]
    udp = ip[(ip[0] & 0x0F) * 4:]
    sport, dport = struct.unpack("!HH", udp[:4])
    payload = udp[8:]
    if sport == PORT and dst == PEER:
        direction = "out"
    elif dport == PORT and src == PEER:
        direction = "in"
    else:
        continue
    ts = None
    for level, kind, value in anc:
        if level == socket.SOL_SOCKET and kind == SO_TIMESTAMPNS:
            sec, nsec = struct.unpack("qq", value[:16])
            ts = sec + nsec / 1e9
    if ts is None:
        ts = time.time()
    ptype = payload[5] if len(payload) > 5 else -1
    seq = struct.unpack("<I", payload[8:12])[0] if len(payload) >= 12 else -1
    stream = struct.unpack("<H", payload[6:8])[0] if len(payload) >= 8 else -1
    rows.append(f"{ts:.9f},{direction},{ptype},{seq},{len(payload)},{stream}")
with open(OUT, "w") as f:
    f.write("\n".join(rows) + "\n")
print(f"captured {len(rows)} packets")
