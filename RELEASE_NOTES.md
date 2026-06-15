# RemSound v4.2

Three fixes — a freeze when connecting, a new-profile glitch that looked like a crash, and smoother audio on WASAPI.

## Connecting no longer risks a freeze

If you had a peer saved by **name** (rather than a numeric address) and that name couldn't be looked up quickly — an offline peer, or a VPN name while the VPN was down — RemSound used to stall for a few seconds while it waited on the lookup. Because a screen reader waits on the program it's reading, that stall could feel like the whole computer locking up: speech going quiet, keys not responding, then everything coming back a moment later. The name lookup now runs in the background, so connecting — and reconnecting — stays responsive no matter what's in your saved-peers list.

## Creating a new profile no longer hides the window

Creating a new profile (**Ctrl+N**), or switching profiles, while you had **Start minimised** turned on would drop the freshly-loaded window straight to the tray — so the new profile looked like it had crashed. "Start minimised" is meant for when RemSound first launches, not for something you did on purpose. Now a new profile (or a switch) comes up the normal way, or stays in the tray only if that's where you already were.

## Smoother audio on WASAPI

RemSound now keeps Windows' timing fine while it's streaming. Without that, Windows can be lazy about waking the audio engine on time, so audio gets delivered in lumpy bursts instead of even steps — which on some machines showed up as breakup, or as latency that crept up over a long session. This fine timing was previously only switched on by **Priority mode**; now the audio path asks for it on its own whenever you're streaming, so you get the smoother delivery without needing that toggle on. (Priority mode's other, heavier options are unchanged and still optional.)

## Compatibility

**v4.2 talks to v3.3 through v4.1 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.2.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.2 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
