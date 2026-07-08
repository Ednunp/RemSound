# RemSound v5.0

A big release. Shape each person you're connected to, record everyone onto separate tracks, give your peers names that stick, and make the window your own with themes and reorderable tabs.

## Shape each peer — volume, pan and EQ

A new **Volume, pan and EQ for peers** tab lets you treat every connected person as their own channel:

- **Volume** — an individual level for each person, so you can balance everyone against each other.
- **Pan** — lean someone to the left or right. It keeps their stereo image; it never folds them down to mono.
- **EQ** — change each person's tone with one of three equalisers: a simple **3-band** tone control, a **12-band graphic** EQ (31 Hz up to 16 kHz), or a **16-band parametric** EQ where you place each band yourself by typing a start frequency, an end frequency and a gain. On the parametric bands list, left and right arrow nudge the selected band by half a dB, live.

Everything is live — you hear it as you move a control — and it adds no delay. One master switch turns it all on or off (there's a global shortcut for that too), and each person has a tick so you can bypass one without losing their settings. Crucially, **what you dial in is captured in your recordings**, so a shaped mix records the way it sounds (with a "record the raw audio" option if you'd rather keep it clean).

## Record everyone on their own track

Recording can now **split into a separate file per peer** — each connected person on their own track — so you can take a jam or a conversation away and mix the parts afterwards. The default recording source is now "both sent and received".

## Name your peers, and see who's who

- **Rename peer (Alt+M or F2)** on the Connectivity tab gives someone a friendly name that sticks to their machine for good — across restarts, address changes and networks — and shows everywhere that peer appears.
- **Peer details (Alt+E)** shows the highlighted peer's name, address, how long you've been connected, the link health and ping, what they're sending (how many devices, WASAPI or ASIO, sample rate and codec), and whether they're receiving your audio.
- **Options → Manage named peers** lists everyone you've named, with where and when you last connected, so you can rename or delete them in one place.

## Make it yours — Appearance

Preferences has a new **Appearance** tab:

- **Colour theme** — Match Windows (the default), Light, or Dark. RemSound now follows your Windows light/dark setting, has a modern font and its own app icon.
- **Tab order** — reorder the main window's tabs with Move up / Move down, and hide the ones you don't use (including the volume/pan/EQ tab and the Discovered / Remembered peer lists on the Connectivity tab).
- **Ctrl and a number** jumps straight to a tab by its position, following whatever order you've set.

## Compatibility

**v5.0 talks to v3.3 through v4.9 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v5.0.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v5.0 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
