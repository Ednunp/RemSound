# RemSound v4.6

Follow your Windows default audio device — for both the sound you receive and the mic you send.

## Use the Windows default audio device

Both WASAPI device lists now have a new entry at the very top — **"Use Windows default audio device, follows Windows changes"** — in the outputs for received sound, and in the inputs to send.

Tick it and RemSound uses whatever Windows currently treats as the default (the default speakers for received sound, the default microphone for sending), and — the useful part — it **follows that default automatically**. Make a headset your default and RemSound switches to it on its own; unplug it and it moves back, all without touching the list.

It works alongside the specific devices: tick the default entry *and* particular cards, and the sound plays out of (or is captured from) all of them at once. When you first turn it on, RemSound offers to untick the other devices so you use only the default — with a "Don't ask me this again" option. If you hide that question and later want it back, use **Options → Reset the default audio device prompt**.

## Compatibility

**v4.6 talks to v3.3 through v4.5 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.6.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.6 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
