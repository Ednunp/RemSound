"""Make the metronome's placeholder click sounds (PLAN-metronome-2026-09-28, "Sounds").

Synthesised, so there is nothing to license: Click, Woodblock, Beep, Rim and Cowbell, each an accent (the first beat of a
bar) and a beat, written to "default sounds/metronome/" as "<name> accent.wav" and "<name> beat.wav". Every file is what
the SHIPPED SOUNDS step demands of a default sound: 16-bit stereo 44.1 kHz PCM, holding only fmt, data and one INFO block
that credits Ed Pourtahmasbi and the RemSound application default sound pack. Each sound starts on its first sample - the
moment the metronome places - so there is no leading silence. Deterministic: the same files every run.

Run from the repository root:  py -3 tools/make-metronome-sounds.py
"""
import math
import os
import random
import struct

RATE = 44100
AUTHOR = "Ed Pourtahmasbi"
PACK = "RemSound application default sound pack"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "default sounds", "metronome")


def decay(t, tau):
    return math.exp(-t / tau)


def render(seconds, fn):
    return [fn(i / RATE, i) for i in range(int(seconds * RATE))]


def click(freq, level, tau, seed):
    rnd = random.Random(seed)
    return render(0.030, lambda t, i: level * decay(t, tau) * (0.75 * math.cos(2 * math.pi * freq * t) + 0.25 * (rnd.random() * 2 - 1)))


def woodblock(freq, level):
    return render(0.120, lambda t, i: level * (0.8 * decay(t, 0.022) * math.cos(2 * math.pi * freq * t)
                                               + 0.2 * decay(t, 0.006) * math.cos(2 * math.pi * freq * 2.71 * t)))


def beep(freq, level):
    length = 0.060

    def f(t, i):
        attack = min(1.0, 0.25 + t / 0.001)
        release = min(1.0, (length - t) / 0.005)
        return level * attack * release * math.cos(2 * math.pi * freq * t)
    return render(length, f)


def rim(level, bright, seed):
    rnd = random.Random(seed)
    state = [0.0]

    def f(t, i):
        # A crude band-pass on the noise: the difference of a sample and a smoothed copy of it.
        n = rnd.random() * 2 - 1
        state[0] += 0.35 * (n - state[0])
        noise = (n - state[0]) * decay(t, 0.004)
        tones = decay(t, 0.012) * (0.6 * math.cos(2 * math.pi * 500 * t) + bright * math.cos(2 * math.pi * 1800 * t))
        return level * (0.55 * noise + 0.45 * tones)
    return render(0.060, f)


def cowbell(f1, f2, level):
    def square(freq, t):
        # Odd harmonics up to about 8 kHz: a softened square, as the 808's cowbell.
        s, k = 0.0, 1
        while freq * k < 8000:
            s += math.cos(2 * math.pi * freq * k * t) / k
            k += 2
        return s * 0.7

    def f(t, i):
        env = 0.65 * decay(t, 0.012) + 0.35 * decay(t, 0.070)
        return level * env * 0.5 * (square(f1, t) + square(f2, t))
    return render(0.250, f)


SOUNDS = {
    "Click": (click(2000, 0.55, 0.0045, 1), click(1500, 0.40, 0.0035, 2)),
    "Woodblock": (woodblock(1100, 0.55), woodblock(850, 0.42)),
    "Beep": (beep(1760, 0.45), beep(880, 0.38)),
    "Rim": (rim(0.60, 0.9, 3), rim(0.45, 0.4, 4)),
    "Cowbell": (cowbell(587, 845, 0.45), cowbell(540, 800, 0.36)),
}


def info_block(name):
    body = b"INFO"
    for fid, text in (("INAM", name), ("IART", AUTHOR), ("IPRD", PACK), ("ICMT", PACK), ("ICOP", "(c) " + AUTHOR)):
        data = text.encode("ascii") + b"\0"
        if len(data) % 2:
            data += b"\0"
        body += fid.encode("ascii") + struct.pack("<I", len(data)) + data
    return body


def write(path, name, samples):
    peak = max(abs(s) for s in samples)
    assert 0.05 < peak < 0.99, (name, peak)
    frames = b"".join(struct.pack("<hh", v, v) for v in (int(round(max(-1.0, min(1.0, s)) * 32767)) for s in samples))
    fmt = struct.pack("<HHIIHH", 1, 2, RATE, RATE * 4, 4, 16)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt
    chunks += b"data" + struct.pack("<I", len(frames)) + frames + (b"\0" if len(frames) % 2 else b"")
    info = info_block(name)
    chunks += b"LIST" + struct.pack("<I", len(info)) + info
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks)


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (accent, beat) in SOUNDS.items():
        write(os.path.join(OUT, f"{name} accent.wav"), f"{name} accent", accent)
        write(os.path.join(OUT, f"{name} beat.wav"), f"{name} beat", beat)
    print(f"wrote {2 * len(SOUNDS)} sounds to {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main()
