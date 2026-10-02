"""On this PC: join a relay as a group member that ticks itself (hear myself), send an Audio packet at the ASIO lane's rate
(one every 0.667 ms unless told otherwise) and time every echo. One thread, busy-waiting on the high-resolution clock, so
neither Python's thread switching nor Windows' timer decides when a packet goes or when its echo is seen. The packets are
padding: no sound, and in a group of its own, so nobody else on the server hears them.
Usage: py -3 echo_probe.py <relay host> <port> <seconds> <out file> [packets a second]
Each line of the out file: sequence, time sent, time back (-1 if it never came back)."""
import importlib.util, os, socket, struct, sys, time, uuid

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("relay", os.path.join(HERE, "..", "..", "server", "remsound-relay.py"))
relay = importlib.util.module_from_spec(spec)
sys.modules["relay"] = relay
spec.loader.exec_module(relay)

HOST, PORT, SECONDS, OUT = sys.argv[1], int(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
RATE = float(sys.argv[5]) if len(sys.argv) > 5 else 1500.0
SERVER = (HOST, PORT)
FRAME = 1.0 / RATE
CID = uuid.uuid4().bytes
GROUP = os.urandom(8)


def v1_packet(kind, payload=b"", stream_id=1, seq=1):
    return relay.MAGIC + bytes([relay.V1_VERSION, kind]) + struct.pack("<H", stream_id) + struct.pack("<I", seq) + payload


def v2_packet(kind, payload=b"", stream_id=1, seq=1):
    return relay.MAGIC + bytes([relay.V2_VERSION, kind]) + struct.pack("<H", stream_id) + struct.pack("<I", seq) + CID + payload


def hello():
    name = b"live test probe".ljust(relay.LOBBY_NAME_BYTES, b"\x00")
    return v2_packet(relay.TYPE_LOBBY_HELLO, name + GROUP + bytes([1]) + CID)


s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("0.0.0.0", 0))
s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 * 1024 * 1024)
s.setblocking(False)
clock = time.perf_counter
proved = False


def drain(on_packet):
    while True:
        try:
            data, _ = s.recvfrom(4096)
        except (BlockingIOError, InterruptedError):
            return
        except ConnectionResetError:
            continue
        on_packet(clock(), data)


def setup_packet(_t, data):
    global proved
    if len(data) > 5 and data[5] == relay.TYPE_ADDR_CHECK:
        cookie = data[relay.V1_HEADER_LEN:relay.V1_HEADER_LEN + relay.ADDR_CHECK_COOKIE_LEN]
        s.sendto(v1_packet(relay.TYPE_ADDR_CHECK, cookie), SERVER)
        proved = True


# Join, answer the address check, and let the group settle.
start = clock()
next_hello = start
while clock() - start < 2.0:
    if clock() >= next_hello:
        s.sendto(hello(), SERVER)
        next_hello += 0.25
    drain(setup_packet)
if not proved:
    sys.exit("the relay never sent its address check")

sent, back = {}, {}
pad = bytes(48)


def echo_packet(t, data):
    setup_packet(t, data)
    if len(data) >= 12 and data[5] == relay.TYPE_AUDIO:
        back.setdefault(struct.unpack("<I", data[8:12])[0], t)


seq = 0
t0 = clock() + 0.01
next_hello = t0
end = t0 + SECONDS
while True:
    now = clock()
    if now >= end:
        break
    if now >= next_hello:
        s.sendto(hello(), SERVER)
        next_hello += 1.0
    if now >= t0 + seq * FRAME:
        seq += 1
        s.sendto(v2_packet(relay.TYPE_AUDIO, struct.pack("<d", now) + pad, stream_id=7, seq=seq), SERVER)
        sent[seq] = clock()
    drain(echo_packet)
tail = clock()
while clock() - tail < 0.3:
    drain(echo_packet)
# Goodbye, as RemSound says it when it leaves a server: without it the relay lists the probe until it times out, and the
# next run's "nobody on it" check would see it.
for _ in range(3):
    s.sendto(v2_packet(relay.TYPE_LOBBY_BYE), SERVER)
with open(OUT, "w") as f:
    for q in sorted(sent):
        f.write(f"{q},{sent[q]:.9f},{back.get(q, -1):.9f}\n")
print(f"sent {len(sent)}, back {sum(1 for q in sent if q in back)}")
