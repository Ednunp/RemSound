# RemSound v3.8

A usability release from user reports: you can now create a new profile at any time, the "different passwords" warning stays put, and the manual explains how to pin a connection to a specific IP.

## New profile, any time

There's a new **New profile** item at the top of the **File** menu (and **Ctrl+N**). It opens a fresh, unsaved session, so you can build a profile for a different setup from scratch.

This closes a real gap: if you'd set RemSound to **start in a specific profile**, you booted straight past the profile picker and had **no way to reach a clean slate** to start something new — your only escape was opening an old profile by luck. Now you always can, without unchecking your startup setting. If your current profile has unsaved changes, New profile offers to save them first. The startup picker and the window title now read **"New profile"** as well, where they used to say "blank template".

## The "different passwords" warning stays on screen

When you connect to someone whose password doesn't match yours, RemSound warns you. That warning was being raised from the once-a-second status update, which kept rebuilding the peer list underneath it and knocked it out of the foreground — so it could **flash away before you reached OK**. It now holds still and stays in front until you dismiss it.

## Pin a connection to a specific IP (documentation)

If a computer has more than one IP address and you want to reach **only one** of them, you can — and you always could: use **Add peer by IP** and type the exact address. RemSound then talks to that address and nothing else (no name lookup, so it can't drift to a different IP). The manual now has a section, *"Connecting to one specific IP address (and only that one)"*, explaining the difference between connecting by name and by a fixed IP, and confirming that a profile remembers exactly what you ticked.

## Compatibility

**v3.8 talks to v3.3 through v3.7 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v3.8.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs, recordings, or sounds.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or v3.7:** Help → Check for updates installs v3.8 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but it uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates at all — install by hand using the steps above.
