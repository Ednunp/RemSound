"""LIVE TESTS AGAINST A REAL REMSOUND SERVER (Ed, 2026-10-02: "you are always allowed to deploy remsound server code to the
pi ... it's there for you to test and you should build some tests for it"). Runs from this PC against the relay on a test
box, here Ed's Pi, and writes a report of PASS / FAIL lines:

  1. THE SERVER BOX: reachable over SSH, the relay running, its installed version.
  2. NOBODY ON IT: the relay's last minute had no clients. If anyone is on it, everything else is skipped - a live test
     must never disturb a real session.
  3. THE VERSION: what the box runs against this tree's server/VERSION (a note, not a failure: the box may run a test build).
  4. REMSOUND'S OWN CHECKS THROUGH THE SERVER: the self-test steps that can run against a real relay
     (REMSOUND_TEST_LIVE_RELAY), each on a desktop nobody is looking at, with a throwaway settings folder.
  5. NETWORK TIMING: 30 s of "hear myself" at the ASIO lane's rate (echo_probe.py, padding only, a group of its own) while
     the box timestamps every packet in and out of its network card (pi_sniff.py, read-only). FAILS if the relay holds a
     packet over 1 ms (one in a thousand) or loses more than 0.5 % of them. The hold-ups on the way there and back are
     reported, not judged: they are this PC's and the network's (2026-10-02: Ed's network card, not the server).

The box's address, user and key are in pi-live.local.json beside this file, which git ignores (the repo is public):
  {"host": "192.0.2.10", "user": "...", "key": "C:\\\\Users\\\\...\\\\.ssh\\\\id_...", "port": 47830, "iface": "eth0"}
Usage: py -3 pi_live.py [all|server|timing]   (all by default). Exit code 0 when nothing failed.
"""
import datetime, json, os, re, socket, statistics, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
MODE = sys.argv[1] if len(sys.argv) > 1 else "all"
LIVE_STEPS = [
    "SERVER STATUS THROUGH THE SERVER",
    "HEAR MYSELF THROUGH THE SERVER",
    "JAMULUS-STYLE THROUGH THE SERVER",
    "METRONOME THROUGH THE SERVER",
]
TIMING_SECONDS = 30
HOLD_LIMIT_MS = 1.0
LOSS_LIMIT = 0.005

results = []
lines = []


def say(text=""):
    print(text, flush=True)
    lines.append(text)


def result(status, name, detail):
    results.append((status, name))
    say(f"[{status}] {name} - {detail}")


def finish():
    """The report, whichever way the run ends, and the exit code: 1 when anything failed. A run that reached the server
    leaves its time window behind, so the next run knows its clients from somebody's while they time out."""
    if "run_start" in globals():
        os.makedirs(os.path.join(HERE, "reports"), exist_ok=True)
        json.dump({"address": own_address(), "start": f"{run_start:%Y-%m-%d %H:%M:%S}",
                   "end": f"{datetime.datetime.now() + datetime.timedelta(seconds=5):%Y-%m-%d %H:%M:%S}"},
                  open(os.path.join(HERE, "reports", "last-run.json"), "w", encoding="utf-8"))
    failed = sum(1 for s, _ in results if s == "FAIL")
    say()
    say(f"RESULT: {'PASS' if not failed else 'FAIL'} - {len(results)} checks, {failed} failed")
    reports = os.path.join(HERE, "reports")
    os.makedirs(reports, exist_ok=True)
    path = os.path.join(reports, f"pi-live-{datetime.datetime.now():%Y%m%d-%H%M%S}.txt")
    open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print(f"Report: {path}")
    sys.exit(1 if failed else 0)


settings_path = os.path.join(HERE, "pi-live.local.json")
if not os.path.exists(settings_path):
    sys.exit(f"No settings: put the test server's host, user and key in {settings_path} (see the top of this file).")
cfg = json.load(open(settings_path, encoding="utf-8"))
HOST, USER = cfg["host"], cfg["user"]
KEY = os.path.expandvars(cfg["key"])
PORT = int(cfg.get("port", 47830))
IFACE = cfg.get("iface", "eth0")


def ssh(command, timeout=60):
    return subprocess.run(["ssh", "-i", KEY, "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", f"{USER}@{HOST}", command],
                          capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def scp(src, dst, timeout=60):
    return subprocess.run(["scp", "-q", "-i", KEY, "-o", "BatchMode=yes", src, dst], capture_output=True, text=True, timeout=timeout)


def own_address():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.connect((HOST, PORT))
        return s.getsockname()[0]


def version_numbers(tag):
    m = re.match(r"server-v(\d+(?:\.\d+)*)", (tag or "").strip())
    return tuple(int(x) for x in m.group(1).split(".")) + (0,) * 3 if m else None


def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(len(xs) * p))]


say(f"RemSound live tests against {HOST}:{PORT} - {datetime.datetime.now():%Y-%m-%d %H:%M:%S} - {MODE}")
say()

# 1. The box.
r = ssh("systemctl is-active remsound-relay; cat /etc/remsound-relay/version")
out = r.stdout.split()
if r.returncode not in (0, 3) or not out:
    result("FAIL", "THE SERVER BOX", f"not reachable over SSH ({(r.stderr or '').strip()[:200]})")
    finish()
active, installed = out[0], (out[1] if len(out) > 1 else "?")
if active != "active":
    result("FAIL", "THE SERVER BOX", f"reachable, but the relay is {active}")
    finish()
result("PASS", "THE SERVER BOX", f"reachable, the relay running, version {installed}")

# 2. Nobody on it, now: who joined and has not left or timed out since, from the relay's log, and any phone or older app
# in its last minute's summary. Clients this PC's own earlier run left behind (state in reports\last-run.json, while they
# time out) and probes are not somebody.
STATE = os.path.join(HERE, "reports", "last-run.json")
r = ssh("sudo -n tail -n 5000 /var/log/remsound-relay/remsound-relay.log | grep -E 'event=(stats|client_)'")
stats = [l for l in r.stdout.splitlines() if "event=stats" in l]
v1 = re.search(r"v1_peers=(\S+)", stats[-1]).group(1) if stats else None
if v1 is None:
    result("FAIL", "NOBODY ON IT", f"could not read the relay's log ({(r.stderr or r.stdout).strip()[:200]})")
    finish()
present, names = {}, {}
for line in r.stdout.splitlines():
    when = line[:19]
    if (m := re.search(r"event=stats .*v2_clients=\[(.*)\]", line)):
        # The minute's summary is the baseline (somebody on for hours joined long before the log's tail); the joins and
        # leaves after it bring it up to now.
        listed = dict(re.findall(r"([0-9a-f-]{36})@([^(,\]]+)", m.group(1)))
        present = {c: present.get(c, (None, a)) for c, a in listed.items()}
    elif (m := re.search(r"event=client_joined client_id=(\S+) addr=(\S+)", line)):
        present[m.group(1)] = (when, m.group(2))
    elif (m := re.search(r"event=client_(?:left|idle_expired|turned_out)\S* client_id=(\S+)", line)):
        present.pop(m.group(1), None)
    elif (m := re.search(r"event=client_named client_id=(\S+) name='([^']*)'", line)):
        names[m.group(1)] = m.group(2)
earlier = json.load(open(STATE, encoding="utf-8")) if os.path.exists(STATE) else {}
me = own_address()


def ours(cid):
    when, addr = present[cid]
    return names.get(cid) in ("live test probe", "server check") or (
        when is not None and addr.startswith(me + ":") and earlier.get("start", "~") <= when <= earlier.get("end", ""))


people = [names.get(c, "someone unnamed") for c in present if not ours(c)]
if people or v1 != "[none]":
    who = ", ".join(people) if people else "a phone or older app"
    result("SKIP", "NOBODY ON IT", f"someone is on the server ({who}) - not disturbing them; nothing else run")
    finish()
result("PASS", "NOBODY ON IT", "nobody on the relay" + (" (only clients of this PC's last run, timing out)" if present else ""))
run_start = datetime.datetime.now()

# 3. The version against this tree.
tree = open(os.path.join(REPO, "server", "VERSION"), encoding="utf-8").read().strip()
if version_numbers(installed) and version_numbers(installed)[:3] == version_numbers(tree)[:3]:
    result("PASS", "THE VERSION", f"the box runs {installed}, this tree is {tree}")
else:
    result("NOTE", "THE VERSION", f"the box runs {installed}, this tree is {tree} - the checks below test the box's code, not this tree's")

# 4. RemSound's own checks through the server.
if MODE in ("all", "server"):
    exe = cfg.get("exe")
    for candidate in (exe, os.path.join(REPO, "publish", "RemSound.exe"),
                      os.path.join(REPO, "src", "RemSound.App", "bin", "Debug", "net10.0-windows", "RemSound.exe")):
        if candidate and os.path.exists(candidate):
            exe = candidate
            break
    gate_tools = os.path.join(tempfile.gettempdir(), "rs-gatetools", "GateTools.exe")
    if not os.path.exists(gate_tools):
        subprocess.run(["dotnet", "build", os.path.join(REPO, "tools", "GateTools", "GateTools.csproj"), "-c", "Release",
                        "-o", os.path.dirname(gate_tools), "--nologo", "-v", "q"], capture_output=True, timeout=300)
    hidden = [gate_tools, "hidden"] if os.path.exists(gate_tools) else []
    say(f"   (RemSound: {exe}{'' if hidden else '; the hidden desktop is not available, so windows may show'})")
    env = dict(os.environ, REMSOUND_TEST_LIVE_RELAY=f"{HOST}:{PORT}", REMSOUND_SOURCE_ROOT=REPO)
    for step in LIVE_STEPS:
        config = tempfile.mkdtemp(prefix="rs-live-")
        try:
            t = subprocess.run(hidden + [exe, "--selftest", "--silent", "--only", step, "--config-dir", config], cwd=REPO, env=env,
                               capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
            found = [l.strip() for l in (t.stdout + t.stderr).splitlines() if re.match(r"\s*\[(PASS|FAIL|SKIP)\]", l)]
        except subprocess.TimeoutExpired:
            found = []
        if not found:
            result("FAIL", step.split(":")[0], "the step reported nothing (did not run, or timed out)")
        for f in found:
            status = re.match(r"\[(\w+)\]", f).group(1)
            # "[PASS] NAME: what it checks  (1082 ms)  - what it found": the report keeps what it found.
            said = re.split(r"\(\d+ ms\)\s+-\s+", f, maxsplit=1)
            result(status, "REMSOUND: " + step.split(":")[0], (said[1] if len(said) > 1 else f.split("]", 1)[1]).strip()[:400])
        try:
            import shutil
            shutil.rmtree(config, ignore_errors=True)
        except Exception:
            pass

# 5. Network timing.
if MODE in ("all", "timing"):
    me = own_address()
    work = tempfile.mkdtemp(prefix="rs-live-timing-")
    remote = "/tmp/remsound-live-sniff"
    scp(os.path.join(HERE, "pi_sniff.py"), f"{USER}@{HOST}:{remote}.py")
    sniff = subprocess.Popen(["ssh", "-i", KEY, "-o", "BatchMode=yes", f"{USER}@{HOST}",
                              f"sudo -n python3 {remote}.py {me} {TIMING_SECONDS + 8} {remote}.csv {PORT} {IFACE}"],
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    time.sleep(2)
    probe_csv = os.path.join(work, "probe.csv")
    p = subprocess.run([sys.executable, os.path.join(HERE, "echo_probe.py"), HOST, str(PORT), str(TIMING_SECONDS), probe_csv],
                       capture_output=True, text=True, timeout=TIMING_SECONDS + 60)
    sniff_out = sniff.communicate(timeout=TIMING_SECONDS + 60)[0].strip()
    sniff_csv = os.path.join(work, "sniff.csv")
    scp(f"{USER}@{HOST}:{remote}.csv", sniff_csv)
    ssh(f"sudo -n rm -f {remote}.py {remote}.csv")
    if p.returncode != 0 or not os.path.exists(sniff_csv):
        result("FAIL", "NETWORK TIMING", f"the probe or the capture did not run: {(p.stdout + p.stderr).strip()[:300]} / {sniff_out[:200]}")
    else:
        probe = [l.split(",") for l in open(probe_csv) if l.strip()]
        sent = {int(q): float(a) for q, a, b in probe}
        back = {int(q): float(b) for q, a, b in probe if float(b) > 0}
        pin, pout = {}, {}
        for l in open(sniff_csv):
            f = l.strip().split(",")
            if len(f) >= 6 and f[2] == "2":
                (pin if f[1] == "in" else pout).setdefault(int(f[3]), float(f[0]))
        warm = sorted(sent)[: int(0.3 * 1500)]   # the first moments, before the echo is switched on
        counted = [q for q in sent if q not in warm]
        lost = [q for q in counted if q not in back]
        loss = len(lost) / max(1, len(counted))
        hold = [(pout[q] - pin[q]) * 1000 for q in counted if q in pin and q in pout]
        rtt = [(back[q] - sent[q]) * 1000 for q in counted if q in back]
        qs = sorted(q for q in counted if q in pin and q in pout and q in back)

        def held(times):
            return {b for a, b in zip(qs, qs[1:]) if (times[b] - times[a]) * 1000 > 3}

        there = held(pin) - held(sent)
        back_new = held(back) - held(pout)
        say(f"   (probe: {p.stdout.strip()}; the box: {sniff_out})")
        if not hold:
            result("FAIL", "NETWORK TIMING: THE RELAY", "no packet was seen both arriving and leaving the box")
        else:
            verdict = "PASS" if pct(hold, 0.999) <= HOLD_LIMIT_MS else "FAIL"
            result(verdict, "NETWORK TIMING: THE RELAY", f"held each packet median {statistics.median(hold):.3f} ms, one in a thousand "
                   f"over {pct(hold, 0.999):.3f} ms, longest {max(hold):.3f} ms ({len(hold)} packets; the limit is {HOLD_LIMIT_MS} ms)")
            # The longest holds, by the box's clock, so a stall can be matched with what the box was doing then.
            worst = sorted(((pout[q] - pin[q]) * 1000, pin[q]) for q in counted if q in pin and q in pout)[-3:]
            say("   (longest holds: " + ", ".join(f"{h:.2f} ms at {datetime.datetime.fromtimestamp(t):%H:%M:%S.%f}"[:-3] for h, t in reversed(worst)) + ")")
        result("PASS" if loss <= LOSS_LIMIT else "FAIL", "NETWORK TIMING: NOTHING LOST",
               f"{len(lost)} of {len(counted)} packets never came back ({loss * 100:.2f} %; the limit is {LOSS_LIMIT * 100:.1f} %)")
        if rtt:
            result("NOTE", "NETWORK TIMING: ROUND TRIP", f"this PC to the box and back: median {statistics.median(rtt):.2f} ms, "
                   f"99 in 100 within {pct(rtt, 0.99):.2f} ms, longest {max(rtt):.2f} ms")
        result("NOTE", "NETWORK TIMING: HOLD-UPS", f"over 3 ms in {TIMING_SECONDS} s, new on the way there {len(there)}, new on the way "
               f"back {len(back_new)} (after this PC sent and after the box sent: this PC's network card and the network, not the server)")
    try:
        import shutil
        shutil.rmtree(work, ignore_errors=True)
    except Exception:
        pass

finish()
