# RemSound v4.3

Two additions for screen-reader users — hear the status line on demand — and a tidier home for logging.

## Hear the RemSound status line on demand

RemSound's main window has a status line that updates every second: how long you've been connected, how many peers you have, whether sound is flowing, and how healthy the link is. Usually your screen reader reads it like any other text — but every so often, for reasons that have nothing to do with RemSound, it loses track of it and says there's nothing there.

There's now a hotkey for it. In the Keyboard shortcuts dialog (Ctrl+K), set a key for **Speak the RemSound status information**, and pressing it reads the whole status line aloud through your screen reader, from anywhere — even with RemSound hidden in the tray or another program in front. It's unset to begin with, so you pick the key, and it's there purely for screen-reader users.

It speaks through whichever screen reader you're running — NVDA, JAWS, Window-Eyes, System Access, SuperNova or ZoomText — and falls back to Windows' own built-in speech if none of those is on.

## A Logging tab, with folder housekeeping

The logging controls now have their own **Logging** tab in Preferences, and there are three new ways to keep the logs folder from growing without limit — all off unless you turn them on:

- **Warn at startup if the logs folder is larger than a size you choose** (starts at 100 MB) — a friendly notice, nothing deleted.
- **Delete logs older than a number of days you set** (1 to 30, starts at 14) — tidied automatically when RemSound starts; today's log is always kept.
- **Delete all logs** — one button, with a Yes/No confirm, that clears every log except the one in use.

## Compatibility

**v4.3 talks to v3.3 through v4.2 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.3.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.3 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
