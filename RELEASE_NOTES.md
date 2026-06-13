# RemSound v4.1

A small follow-up to v4.0, with two fixes for screen-reader users.

## Starting in the tray is silent

When RemSound starts straight into the notification area (**Start minimised**, or `--minimized`), it no longer plays the "minimise" cue. Booting into the tray isn't you choosing to hide the window, so it shouldn't sound the cue — only a genuine minimise does.

## Bringing the window back announces itself

When you restore RemSound from the tray, it now lands focus on a control on whichever tab you'd left showing, so your screen reader announces the window instead of surfacing silently.

## Compatibility

**v4.1 talks to v3.3 through v4.0 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.1.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.1 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
