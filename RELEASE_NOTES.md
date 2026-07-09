# RemSound v5.2

A stability and polish release. No new features — this one is bug fixes and internal tidy-ups from a deep code review, so it should just feel a bit more solid.

## Fixes you might notice

- **Changing the codec or send rate while streaming** could, in rare cases, crash RemSound with no warning. Fixed.
- **Recording "both sent and received" into a single file** could lose small amounts of audio and drift out of sync over a long session. It now stays sample-accurate for the whole recording.
- **After installing**, RemSound now honours your "start minimised" setting on the first relaunch (and still comes to the front when you're not starting minimised).
- **Uninstalling** one copy no longer switches off a different copy's "run at startup".
- Starting a split "received only" recording with **no peers connected** now tells you clearly, instead of quietly making an empty folder.
- The **clipping indicator** in the diagnostics works again.

## Under the hood

- The automatic-update restart now brings the window to the front, the same way the installer does.
- The router port opened by UPnP is properly removed when you close RemSound.
- Lighter on memory and disk — fewer background file reads while you work, and tidier release of resources when the window closes or switches profiles.

## Compatibility

Nothing about the over-the-network format changed, so **v5.2 talks to v3.3 through v5.1** with no trouble — you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v5.2.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v5.2 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
