# RemSound v3.5

A reliability-and-tidiness release: sound cards recover on their own if you unplug them, the audio cushion now sizes itself to each card, everything you own lives in one neat folder that updates can't touch, and a stack of under-the-hood fixes from a top-to-bottom code review.

## Recover a sound card you unplug

If a USB sound card you're listening through is unplugged and plugged back in, **RemSound now re-opens it automatically and the sound resumes** — you don't have to re-tick it. (This works when the card comes back as the same Windows device, which is the usual case when you plug it into the same socket.)

## The audio cushion sizes itself to each card

RemSound keeps a small cushion at the sound card to smooth over the tiny timing difference between two machines' sound clocks. It now **sizes that cushion to each card automatically** — a card that moves sound in bigger chunks gets a little more room, a fast interface stays tight — so there's nothing to fiddle with. It just settles on the right amount.

## A microphone-privacy heads-up

Windows can silently block apps from using your microphone, and when it does, a mic sends silence rather than failing — easy to miss. RemSound now **warns you** when you switch on a mic Windows is blocking (including when a profile loads with one already on), and points you at the exact two Windows settings to turn on.

## Warnings always come to the front

Even when RemSound is minimised to the system tray, an important warning — the "your files have moved" notice, a microphone warning, an update prompt — now **pops up in front with focus**, so your screen reader reads it straight away. RemSound stays in the tray; only the warning comes forward.

## One tidy folder for everything

Everything this machine keeps for you — your settings, profiles, logs, and cue sounds — now lives together in one folder inside RemSound called **user settings and logs**. It moves there automatically the first time you run v3.5, and RemSound tells you once. Nothing is lost. **From now on, updates never touch that folder** — so any custom cue sounds you put there survive updates.

## Smaller things

- **Volume and mute** now come back correctly when you load a profile.
- A round of reliability fixes from a full code review: two resource leaks closed, profile and settings saves are now crash-safe, and a good clear-out of dead code.

## Compatibility

**v3.5 talks to v3.3 and v3.4 with no trouble** — the over-the-network format hasn't changed, so you don't have to update both ends at once. (You still need everyone on **v3.3 or newer**, because that's where end-to-end encryption came in.)

## Install

1. Download `RemSound-v3.5.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your profiles, settings, recordings, or sounds.
4. Run `RemSound.exe`. The first launch tidies your files into the new folder and tells you once.

## Upgrading

**From v1.9 onward:** Help → Check for updates works — it will fetch and install v3.5 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.5 installs itself shortly after launch.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it installing updates, so Check for updates will download v3.5 but not apply it. Install v3.5 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
