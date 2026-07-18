# RemSound v5.5

A close-hang fix and a keyboard-accessibility sweep.

## Fixed: a hang on close when UPnP is on

With the "open my router's port automatically" (UPnP) option turned on, closing RemSound could **hang instead of shutting**. On the way out RemSound asks the router to close the port again, and that request waits for the router to answer — a slow or fussy router (or a double-NAT setup) left the window stuck.

Closing now **never waits on the router**: the tidy-up runs in the background with a short time limit, so RemSound shuts straight away and lets the router expire the port on its own if it's being slow. (Under the hood the app also logs each router step now, so if anything ever does stall we can see exactly which call was slow.)

## Keyboard shortcuts on every dialog

Every button in every pop-up dialog now has an **Alt-key shortcut** — including the "RemSound is already running" dialog, which previously had none at all. So you can always pick an option straight from the keyboard rather than tabbing to it. (Where a dialog's Cancel would clash with another shortcut, Cancel stays on Escape, as before.)

## Compatibility

Nothing about the over-the-network format changed, so **v5.5 talks to v3.3 through v5.4** with no trouble — you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v5.5.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v5.5 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
