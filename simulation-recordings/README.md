# Network recordings for the simulator's calibration check

Every `.txt` file in this folder is a network recording. The gate step SIMULATOR CALIBRATION replays each one as it was
made and compares what the simulated receiver hears with what the real receiver heard in that minute.

## Adding a real one

1. In RemSound, straight after a minute worth keeping (clean or not): **Record > Save the last minute of the network**.
2. The file lands in the logs folder as `RemSound-network-<computer>-<date-time>.txt`.
3. Copy it here. Give it a name that says what it is, for example `ed-desktop-from-andre-via-pi-5ms.txt`.

The file holds the addresses of the computers that were connected, like a log does, so a real one is never committed:
the repository's `.gitignore` keeps every `.txt` here out except the simulator's own `simulated-*.txt`. The calibration
check still reads them all on the machine that has them.

## What is compared

Per minute: audible gaps, late or doubled packets dropped, and gaps the codec could not repair. The simulator agrees
when each is within 3 a minute, or within half of the larger figure.

If the auto-tune moved the buffer during the minute, the recording only knows the buffer at the moment it was saved, so
turn the auto-tune off for a recording meant for calibration.

## Running it by hand

- `RemSound.exe --simulate calibrate` checks every recording here.
- `RemSound.exe --simulate replay --recording <file>` replays one, then tries other codecs and buffers on it.
- `RemSound.exe --simulate record --out <this folder>` remakes the simulated seed recordings.

## What is here

- `simulated-laptop-lumps-asio-10ms.txt`: made by the simulator itself (a lumpy laptop at a 10 ms buffer). It proves the
  check works and can fail. Real recordings from Ed and Andre go beside it.
