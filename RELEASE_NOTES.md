# RemSound v3.9

A reliability release built from real reports: a long-standing cause of one-way silence is fixed, long sessions no longer drift slower, RemSound stops "sending" when nobody is connected, there's a start-up sound, and you can now drive RemSound from the command line.

## One-way silence is fixed

If you sent plain (non-ASIO) audio to someone who had an **ASIO device selected** at their end, your sound — your microphone included — could arrive at their machine and then never be played. They heard nothing, even though everything looked connected and their logs showed audio coming in.

This was a routing fault inside the receiver: a plain stream arriving at a receiver running in two-lane (ASIO) mode was being decoded into a buffer that nothing ever played out, so it silently piled up and was dropped. A plain stream now always reaches the speakers, whatever mix of ASIO and standard audio the two of you happen to be using. This is the fix behind "my mic works for me but they can't hear it."

## Long sessions stay as tight as they started

On the standard (non-ASIO) path the incoming audio buffer used to creep deeper after a network hiccup and never settle back, so a connection that started tight could feel laggy hours later. It now eases itself back to your chosen latency, gently and silently, so a long session stays as tight as it began.

## RemSound no longer "sends into the void"

If you left a profile open with nobody connected, the status line could still report that it was sending data — and a large running total — which was confusing and looked wrong. RemSound now only sends audio once a peer is genuinely reachable. With no one connected it sits quietly, and the status line reflects that. (Reported on the issue tracker.)

## A start-up sound

RemSound now plays a short sound as it starts, so you know it's running even when it opens straight to the notification area. You can turn it off, or choose your own sound, under **Options → Preferences**.

## Run RemSound from the command line

As well as its normal window, RemSound now takes **command-line options** — handy for quick checks, for getting a support report to send, and for starting RemSound a particular way from a shortcut or a script:

* `--devices` lists every microphone, output and ASIO driver, with formats and ids.
* `--selftest` runs the whole audio path on the machine on its own and reports PASS or FAIL.
* `--diagnostics` writes one report file (version, system, settings, profiles, devices, mic-privacy check, recent log) to send for help.
* `--profile`, `--connect` and `--minimized` start RemSound straight into a profile, connected to an address, or down in the tray.

Run `RemSound.exe --help` for the full list, or see the new **Command-line options** section in the manual (press F1).

## Compatibility

**v3.9 talks to v3.3 through v3.8 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v3.9.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs, recordings, or sounds.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6, v3.7 or v3.8:** Help → Check for updates installs v3.9 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but it uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates at all — install by hand using the steps above.
