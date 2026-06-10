# RemSound v3.7

A smoothness-and-diagnosis release: changing devices mid-session no longer crackles, a one-off computer hiccup no longer balloons your latency, the microphone-privacy warning catches the hidden kinds of block, and the log now records enough to prove what happened when something goes wrong.

## Changing devices mid-session is smoother

When you change capture devices — ticking sources on or off, or bringing an ASIO driver in or out — RemSound used to rebuild its sound engine immediately on every single change. A quick run of changes meant several rebuilds back to back, each one an audible crackle. Now RemSound **waits a moment for you to finish and rebuilds once**, to the final state. Reconfiguring no longer fights you.

## The auto-tuner ignores one-off hiccups

Continuous auto-tune sizes your latency cushion from how late packets are arriving. It used to react to the **single worst moment** it had seen — so one brief stall (the kind a driver or Windows hiccup causes once and never again) could balloon the cushion to its maximum and disrupt audio for far longer than the hiccup itself. It now only raises the cushion when late audio **keeps coming** — sustained jitter still gets a fast response, a lone blip gets ignored.

## The mic-privacy warning catches hidden blocks

Windows can block an app's microphone in ways that don't show up as the two obvious switches: a per-app block aimed at one program alone, or a block set by an administrator or workplace policy with no visible switch at all. When one of those is in place, the mic silently sends nothing — and RemSound's warning used to miss both kinds. It now **catches them too**, so "my mic sends silence and nothing warned me" should be gone.

## The log can now prove what happened

Four additions to RemSound's diagnostic log, aimed squarely at the questions we couldn't answer from past logs:

- **How loud is what you're sending** (`capPeak` each second) — so silence versus real audio is now a fact in the log, not a guess.
- **The mic-privacy check's verdict** at every startup — whether or not it warned.
- **Every device tick/untick you make**, and **every device change Windows reports** — so when the device set changes mid-session, the log shows exactly what drove it.
- Auto-tune now logs both the worst moment it saw and the value it actually acted on, so you can see when it ignored a one-off spike.

Nothing about these is audible — they just make the next bug report solvable from the first log.

## Compatibility

**v3.7 talks to v3.3 through v3.6 with no trouble** — the over-the-network format hasn't changed, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v3.7.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs, recordings, or sounds.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6:** Help → Check for updates installs v3.7 with the new in-app updater — and if it ever can't finish, it puts v3.6 back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but it uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above — every update after that uses the new installer.

**v1.8 and earlier:** the auto-updater in those versions can't install updates at all — install by hand using the steps above.
