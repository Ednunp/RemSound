# RemSound v4.0

A big step up for audible feedback and for getting the latency right by itself. RemSound now has a sound for nearly everything you do, can click as you type, has a clearer four-tab Preferences window, and tunes latency more intelligently and more quietly.

## A sound for nearly everything

On top of the connect, disconnect and recording cues, RemSound can now play a short sound when you:

* turn **sending** or **receiving** on or off (a separate sound for each),
* **minimise** to or **return** from the notification area,
* **tick or untick** any box, and
* **switch between tabs**.

Every one is optional. Under **Options → Preferences → Audio cues** you can silence any cue, pick which built-in sound it uses, or **Browse** for your own WAV file. (The receive-audio on/off sounds were a request on the issue tracker — they're here now.)

## Hear yourself type

RemSound can **click softly as you type** into any box, so you get an audible sense of your keystrokes — with a distinct sound in **password fields** so you always know which kind of box you're in. One tick under Audio cues turns it on or off.

## Preferences, reorganised

The Preferences window is now four clear tabs — **General**, **Audio cues**, **Startup behaviour** and **Update settings** — so everything is easier to find. The startup options (start with Windows, start minimised, start with a chosen profile) have moved here from the Options menu.

## Smarter, quieter latency tuning

The automatic latency tuning used to treat every tiny audio glitch as "the buffer is too small" and keep adding delay — even when the real cause was the receiving computer's own sound card stumbling, which more buffer can't fix. It now tells the two apart, so it stops piling on delay it can't help. And when it does lower the latency, it eases the buffer down smoothly instead of trimming it, so you no longer hear little clicks while it tunes.

## Default sounds that updates can refresh

The built-in default sounds now travel with the program itself, so an update can refresh them — if a better default sound ships in a future update, you'll actually get it. Your own chosen sounds are kept exactly as you set them.

## Compatibility

**v4.0 talks to v3.3 through v3.9 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.0.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.0 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was. The first launch tidies your settings into their current home if they aren't there already; nothing is lost.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
