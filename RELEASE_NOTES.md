# RemSound v5.3

Send a single application's sound, and stream a machine from its lock screen. Plus freer use of pro (ASIO) sound cards.

## Send one application, not the whole device

Until now you sent a whole audio device — everything coming out of your PC. In the send list you can now choose a **specific application** instead: just your music player, just your game, just a call. Only that program's sound goes out; the rest of your PC stays private.

- Tick any app that's **currently making sound**, or **name an app** so RemSound catches it the moment it opens.
- Whole-device sending works exactly as before — this is an extra choice, not a replacement.

## Stream from the lock screen, before anyone signs in

There's a new, **optional RemSound service**. It can send a machine's audio from the Windows **lock screen** — so you can hear a computer that's powered on but not yet logged in, which the normal app can't do because nothing runs until you sign in.

- Install it **once**, from the RemSound installer (it offers to set it up for you). It needs administrator rights that one time only.
- It runs quietly in the background and **steps aside automatically** the instant you open RemSound normally, handing the audio back to the app.
- It **updates itself** whenever RemSound updates — there's nothing to maintain.

If you never want it, don't install it: nothing about the normal app changes.

## Freer with professional (ASIO) sound cards

- You can now **switch between ASIO drivers** while RemSound is open.
- RemSound **releases the sound card** when it isn't using it, so another program (a DAW, for example) can take it over instead of being locked out.

## Also

- Per-application sending is fully wired through the app, and a range of fixes and internal tidy-ups from continued review.

## Compatibility

Nothing about the over-the-network format changed, so **v5.3 talks to v3.3 through v5.2** with no trouble — you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v5.3.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v5.3 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
