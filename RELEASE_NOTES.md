# RemSound v3.4

A round of quality-of-life and reliability improvements: a fast new way to switch profiles from anywhere, a safety net for the troublesome Realtek ASIO driver, your screen reader now reads out global hotkeys, and WASAPI connections stay rock-steady over long sessions.

## Quick profile switch

There's a new global hotkey — **Quick profile switch** — that pops up a small list of all your profiles, wherever you are and whatever you're doing. Arrow to the one you want, press Enter, and RemSound switches straight to it.

- The profile you're currently on is marked in the list.
- A sound plays as the list opens, and the switch cue plays the moment you pick one.
- If RemSound was tucked away in the system tray, **it stays there** — no window jumping up in front of whatever you're working on.

It's unset by default. Give it a key under **Options → Keyboard shortcuts**.

## A safety net for the Realtek ASIO driver

Realtek's bundled ASIO driver leaks Windows resources every time it's opened, and can make audio unstable. RemSound now **spots it on startup and offers, just once, to disable it** — say yes and RemSound will never touch that driver again. If you'd rather keep it, RemSound won't nag you about it after that first time.

You can flip it back on, or off, whenever you like from **Options → Enable / Disable Realtek ASIO driver in RemSound**.

## Your screen reader now reads out global hotkeys

When you move over a menu item, or a control that has a global hotkey assigned, your screen reader now reads the hotkey along with everything else — for example, "press Ctrl+Shift+M anywhere". So you can learn and double-check your shortcuts just by arrowing around the window, without opening the shortcuts dialog.

## Smoother long sessions on WASAPI

On WASAPI — the ordinary Windows sound path — two machines' sound clocks run at very slightly different speeds. Over a long session that tiny difference used to add up, and the delay would slowly drift. RemSound now measures and gently corrects that drift the whole time, so a WASAPI connection is as tight at the end of a three-hour session as it was at the start. (ASIO already kept itself in step; this brings WASAPI up to the same standard.)

## Smaller improvements

- **A new "profile menu open" cue** sounds when the Quick profile switch popup appears. Like every other cue, it has its own mute toggle and custom-sound option in Preferences.
- **The profile-switch cue now plays the instant you switch**, and no longer plays on a fresh start into your first profile — so you won't hear it stacked on top of the connect sound at launch.
- **Faster device detection.** RemSound now reacts the moment you plug in or unplug a sound device, instead of checking every few seconds. This also closed a slow resource leak on the receiving side.
- **Tidier folder.** Your settings and profiles now live in a `config` folder inside RemSound. The move happens automatically the first time you run v3.4, and RemSound tells you once that it's done it. Nothing is lost, and everything works exactly as before.

## Compatibility

**v3.4 talks to v3.3 with no trouble** — the over-the-network format hasn't changed, so you don't have to update both ends at once. (You still need everyone on **v3.3 or newer**, because that's where end-to-end encryption came in.)

## Install

1. Download `RemSound-v3.4.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your profiles, settings or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v1.9 onward:** Help → Check for updates works — it will fetch and install v3.4 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.4 installs itself shortly after launch.

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it installing updates, so Check for updates will download v3.4 but not apply it. Install v3.4 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
