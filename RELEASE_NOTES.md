# RemSound v5.4

Service polish and an important install fix. If you use the lock-screen service, this is a worthwhile update; if you don't, the "Use Windows default output" option and its tidier behaviour still apply to the main app.

## Use the Windows default output

Both the service and the main app can now send **"Use Windows default audio device"** — pick it and RemSound sends whatever this machine is currently playing, and **follows the Windows default** if you change it later (handy for the service: tell a machine "send whatever it plays" without pinning a named card).

Ticking it is now **exclusive**: it unticks the specific cards in that list and locks them out until you turn the default entry back off. In each list you're clearly either following the default or picking devices — never a confusing mix. (This replaces the old optional "shall I untick the others?" prompt.)

## Fixed: a service-install freeze

Installing or reinstalling the service could **hang and lock RemSound up** — the window froze and its audio stopped, though the connection stayed alive. That's fixed at the root (a pipe deadlock in the elevated installer), and the install now runs so that a slow or stuck step **can never freeze the app again**.

Straight after installing, RemSound now also asks whether you'd like to **start the service right away** (it otherwise waits until the next reboot).

## Service tidy-up

The service now sends this machine's **output audio, or specific applications** only — the microphone-inputs list has been removed, since capturing a mic from a logged-out machine isn't what the service is for. (The main app still has its full inputs list.)

## Compatibility

Nothing about the over-the-network format changed, so **v5.4 talks to v3.3 through v5.3** with no trouble — you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v5.4.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v5.4 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
