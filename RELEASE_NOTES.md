# RemSound v4.7

Sound comes back on its own after a reboot — plus a few screen-reader and status improvements.

## Sound returns by itself after a reboot

If RemSound started up before your network or VPN was ready, it could lock onto the wrong machine on your network and sit silent until you closed and reopened it. RemSound now keeps trying the peer you actually chose and connects the moment it answers — no manual reconnect needed.

(With thanks to the singer who reported this and captured the log that pinned it down.)

## Hearing the status, improved

The **Speak the RemSound status information** hotkey (for screen-reader users) now:

- reads the status **a line at a time** — peers, ping, uptime, rates, totals — so each part lands as its own short phrase;
- shows big data totals in **gigabytes** once they pass a gigabyte;
- copies the whole status to the **clipboard** on a quick **double press**, so you can paste it to someone.

The status line now also shows **how much CPU and memory RemSound itself is using**.

## If RemSound won't start: Install Scripts

RemSound needs the .NET 10 desktop runtime. On the rare machine that doesn't have it, RemSound can't start to install it itself — so there's now an **Install Scripts** folder next to the program containing a one-click installer (a `.cmd` and a `.ps1`) that fetches and installs the runtime for you.

## Other fixes

- The **"what's new"** notes no longer appear a second time after an update that didn't finish.

## Compatibility

**v4.7 talks to v3.3 through v4.6 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.7.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.7 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
