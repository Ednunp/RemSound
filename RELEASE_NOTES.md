# RemSound v3.6

A reliability release centred on updating. RemSound now installs its own updates instead of handing the job to a Windows script — so updates that used to quietly fail on some machines go through, and a failed update can never leave you half-installed.

## Updating is far more reliable

RemSound used to finish an update by handing the file-swap to a small Windows batch script. On some machines that step would silently fail — you'd "update" and still be on the old version, sometimes with nothing on screen to say why.

**RemSound now installs updates itself, in its own code:**

- It runs the install from a fresh copy of the new version in a temporary folder, so nothing in the program folder is locked while the swap happens.
- It waits for the old copy to fully close, then moves the old files aside and copies the new ones in — **retrying** if a file is briefly held open (by a sync app, say) rather than giving up.
- **If anything goes wrong, it puts your previous version back exactly as it was** and reopens it. A failed update can never leave a broken, half-installed RemSound.
- Every step is written to a plain `updater.log` next to the program, so if a problem ever does recur there's a real reason to read, not a blank.

Your settings, profiles, logs and cue sounds are never touched by any of this.

## More warnings come to the front

Every important notice now surfaces in front with focus, even when RemSound is minimised to the tray. v3.6 adds the last two that didn't: the **"RemSound is already running"** dialog (and its follow-up message), and the **what's-new note shown after an update** that installed while RemSound was tucked away.

## Compatibility

**v3.6 talks to v3.3, v3.4 and v3.5 with no trouble** — the over-the-network format hasn't changed, so you don't have to update both ends at once. (Everyone still needs to be on **v3.3 or newer**, because that's where end-to-end encryption came in.)

## Install

1. Download `RemSound-v3.6.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs, recordings, or sounds.
4. Run `RemSound.exe`.

## Upgrading

**From v1.9 onward:** Help → Check for updates works — it will fetch and install v3.6 automatically, and if you've ticked "Check for updates on startup" and "Silently install updates" it installs itself shortly after launch.

Note that the hop *to* v3.6 still uses your current version's updater. The new, more reliable in-app installer only takes over **once you're on v3.6**. So on the rare machine where auto-update has been failing — exactly the problem this release fixes — download `RemSound-v3.6.zip` and extract it over your folder by hand, just this once. Every update after that uses the new installer.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it installing updates at all — install v3.6 by hand using the steps above.
