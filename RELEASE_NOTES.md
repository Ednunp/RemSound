# RemSound v4.8

A rare crash fixed, and crash reports for next time.

## Fixed: a rare unexpected close

If a peer was reachable at two addresses at once — for example a transmitter on a VPN *and* the local network at the same time — RemSound could rapidly flip the connection back and forth between the two addresses. Usually harmless, but a fast enough flip could tear the audio down and rebuild it quickly enough to make RemSound close unexpectedly: you'd find the sound had stopped and the program gone, and relaunching brought it straight back.

RemSound now settles on whichever address is actually working and stays there, only following a genuine move once the current address has properly stopped responding — so the flipping, and the crash it could cause, are gone.

(With thanks again to the singer who reported it and sent the log.)

## New: a crash file if anything ever does go wrong

If RemSound ever closes unexpectedly, it now writes a small `crash-*.txt` file into your logs folder (RemSound folder → **user settings and logs** → **logs**). There's nothing you need to do with it — but if you ever hit a problem, sending that file in turns a "closed for no reason" into something that can actually be pinned down.

## Compatibility

**v4.8 talks to v3.3 through v4.7 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.8.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.8 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
