# RemSound — user manual

RemSound is a Windows program that sends live sound from one computer to another, with very little delay. Picture a private audio link between two or more computers: each one decides what sound it wants to send and what sound it wants to play, and the audio travels straight between them over your network.

I built RemSound to solve a specific problem of my own. I do a lot of work on a powerful computer that I control remotely from a lighter machine, and I wanted to actually hear that powerful machine's audio on the laptop in front of me. There are other programs that can move sound between PCs, but none of them did it quite the way I wanted, so I made my own. It turns out the same thing is just as handy for listening to one room from another room in your house, playing music together over the internet with very little delay, co-hosting a podcast, or anything else where you want to get sound from one PC to another, fast.

## Table of contents



  1. [What RemSound does](#1-what-remsound-does)
  1. [Quick start](#2-quick-start)
  1. [Profiles](#3-profiles)
  1. [The main window: menu bar and tabs](#4-the-main-window-menu-bar-and-tabs)
  1. [Menus (File, Record, Options, Service, Help)](#5-menus-file-record-options-service-help)
  1. [Connectivity tab](#6-connectivity-tab)
  1. [Audio inputs and outputs tab](#7-audio-inputs-and-outputs-tab)
  1. [Audio profile tab](#8-audio-profile-tab)
  1. [Volume, pan and EQ for peers tab](#9-volume-pan-and-eq-for-peers-tab)
  1. [ASIO and WASAPI](#10-asio-and-wasapi)
  1. [Peers — finding and connecting](#11-peers--finding-and-connecting)
  1. [How the network works (LAN, WAN, Tailscale)](#12-how-the-network-works-lan-wan-tailscale)
  1. [Passwords and encryption](#13-passwords-and-encryption)
  1. [Latency and audio quality](#14-latency-and-audio-quality)
  1. [Keyboard shortcuts (within the main window)](#15-keyboard-shortcuts-within-the-main-window)
  1. [Global hotkeys (work even when minimised)](#16-global-hotkeys-work-even-when-minimised)
  1. [Remote control: adjusting a peer's listening volume from your end](#17-remote-control-adjusting-a-peers-listening-volume-from-your-end)
  1. [Startup behaviour](#18-startup-behaviour)
  1. [Audio cue sounds](#19-audio-cue-sounds)
  1. [Updating RemSound](#20-updating-remsound)
  1. [Recording to a file](#21-recording-to-a-file)
  1. [Logs and diagnostics](#22-logs-and-diagnostics)
  1. [Command-line options](#23-command-line-options)
  1. [The lock-screen service (send only)](#24-the-lock-screen-service-send-only)
  1. [Troubleshooting](#25-troubleshooting)
  1. [Glossary](#26-glossary)


## 1. What RemSound does

RemSound carries sound from one PC's microphone or sound card to another PC's speakers or audio interface, almost instantly, over your network. Both computers run the same program, and each one decides for itself whether it wants to send sound, receive sound, or do both.

### The basic flow

Step| What happens
---|---
1| You tick “Send my audio” on the Audio inputs and outputs tab and choose which microphone or sound output you want sent.
2| Your friend ticks “Receive audio” on the same tab and chooses which speakers or headphones should play the sound they receive.
3| One of you ticks the other person in the Discovered peers list on the Connectivity tab (or types their address by hand).
4| Sound starts flowing. The other direction works exactly the same way, on its own — both of you can speak at the same time.

There is no central server, no account, and nothing stored online. The sound goes straight from one computer to the other.

> **RemSound on Android (receiver):** there is a companion app that lets a phone or tablet _receive_ RemSound audio — handy for listening on the move. It's a separate community project built and maintained by Aryan Choudhary, who is a screen-reader user himself and has tuned the app for TalkBack; it is not part of RemSound and is not maintained by us. Get the signed app from its releases page (download the latest **app-release.apk**): [RemSound Android — Releases](https://github.com/aryanchoudharypro/RemSoundAndroid/releases).

> **RemSound on iOS (beta):** there is also a companion app for iPhone and iPad, currently in beta testing on Apple's TestFlight. Like the Android app it's a separate community project — built and maintained by Jonathan Schuster — and is not part of RemSound and not maintained by us, but it speaks the same protocol. Join the beta here: [RemSound for iOS on TestFlight](https://testflight.apple.com/join/pNCnj3z2).

## 2. Quick start

> **If RemSound won't start** — if Windows says it needs “.NET” — RemSound runs on the **Microsoft .NET 10 Desktop Runtime**. Most up-to-date Windows machines already have it; if yours doesn't, open the **Install Scripts** folder sitting next to `RemSound.exe` and double-click **Install .NET for RemSound.cmd**. It fetches and installs it for you (using winget, the Windows package manager), and then RemSound will start. There's a `.ps1` version in the same folder for PowerShell users. If you'd rather do it by hand, the script opens Microsoft's download page, where you pick “.NET Desktop Runtime” for “x64”.

Let's assume you and a friend both have RemSound running, and that your two computers can reach each other on the network (the same Wi-Fi, the same Tailscale account, and so on).

  1. Start RemSound. The first thing you'll see is the **profile picker**. On a brand-new install your only choice is **New profile** — select it and press Enter or click OK. Later, once you've saved a setup or two of your own, this is the dialog where you choose which one to load. See Profiles for the full story.
  2. Once the main window opens, go to the **Audio inputs and outputs** tab. Tick **Receive audio (Alt+R)** , then tick the device you want incoming sound played through in **WASAPI outputs for received audio (Alt+3)**.
  3. On the same tab, tick **Send my audio (Alt+S)** and tick your microphone in **WASAPI audio inputs to send (Alt+5)**.
  4. Go to the **Connectivity** tab, find your friend in the **Discovered peers (Alt+D)** list, and tick them. If they aren't showing up, use **Add peer by IP (Alt+A)** and type their address.
  5. Have your friend do the same with you on their computer.
  6. Within a second or two, both of you will hear each other.
  7. In the **File menu** (Alt+F), choose **Save as** to give your setup a name. Next time you start the program, picking that name from the startup dialog restores all your settings, device choices, peers, and connections in one go.



> **If you have a professional audio interface (Audient, Komplete Audio, RME, Focusrite, and the like):** on the **Audio inputs and outputs** tab pick your driver in the **ASIO driver (Alt+D)** list to use its low-delay channels. Choosing a real driver makes the ASIO device lists appear; choosing _(none)_ hides them again and the app uses the ordinary Windows sound path only. See ASIO and WASAPI below.

## 3. Profiles

RemSound saves your whole setup — which devices are ticked, whether you're sending or receiving, your sound quality settings, delay targets, your ASIO driver choice, remembered peers, currently connected peers — into a single settings file. Each saved setup is called a _profile_. You choose which profile to load every time you start the program. You might keep one profile for “morning podcast” and another for “evening jam session”, each with a different mix of devices ticked, and switch between them in a couple of clicks. (Your keyboard shortcuts are the one thing that _isn't_ saved per profile — from version 4.4 they're shared across all your profiles; see Global hotkeys.)

### The startup picker

When RemSound starts, the first thing you see is the profile picker. It is a list of your saved profile names, with an extra entry called **New profile** at the top. The keys are deliberately simple:

Key| Action
---|---
Up / Down| Move between profiles in the list.
Enter| Load the highlighted profile (or a new profile) and open the main window.
OK button| Same as Enter.
Del| Delete the highlighted profile, after a yes / no confirmation.
Browse… button| Choose a different folder to read profiles from. Handy if you keep your profiles in Dropbox or another sync folder so they follow you between computers. Your choice is remembered next time RemSound starts.
Esc| Deliberately does nothing here. You have to pick a profile to start the program.
Alt+F4| Closes the dialog and quits RemSound — in other words, “I don't want to start the program right now.”

The first profile in the list is highlighted to begin with, so on a fresh install where the only entry is **New profile** , you just press Enter to get going.

### What “New profile” means

A new profile is a one-off session with all the defaults: nothing ticked in any device list, neither Receive nor Send turned on, the standard sound settings, no ASIO driver chosen, and no remembered peers. (Your keyboard shortcuts aren't part of a profile, so starting a new profile doesn't change them.) You'd pick it for a quick session you don't plan to save, or as a clean starting point for a new profile. The Save button is hidden while you're on a new profile — there's no existing setup to update, only a new one to save.

**Starting a new profile later:** the picker only appears at startup, and if you've set RemSound to _start in a specific profile_ you skip straight past it. So to start a brand-new profile at any time, use **File → New profile** (Ctrl+N, or Alt+F, W) — it opens a fresh, default session, ready for you to set up and then Save as. If your current profile has unsaved changes, it offers to save them first.

### Saving and updating

The File menu has two ways to save:

Item| What it does
---|---
**File → Save (Ctrl+S)**| Updates the current profile with whatever your settings are right now. If you're on a new profile, this turns into Save as instead, because there's no existing profile to update.
**File → Save as… (Alt+F, A)**| Always available. Asks you for a name. From a new profile, this is how you create your first profile. From an existing profile, it makes a fresh copy under a new name and switches to that copy.

The window title bar always shows which profile is in use: `RemSound — Active profile: My session name`.

### Switching, renaming, and deleting

Action| How
---|---
**Switch to a different profile**|  File → Open profile (Alt+F, O). Pick a profile in the file picker. RemSound reloads using that profile.
**Rename the current profile**|  File → Rename current profile (Alt+F, M). It asks for the new name and renames the profile's file; the window title updates straight away.
**Delete a profile**|  File → Open profile, then right-click the entry in the Windows file picker and choose Delete. RemSound lets Windows handle this rather than having its own delete button.

### Where your files are stored

Everything RemSound keeps for you on this computer — your settings, your profiles, and your logs — lives together in one folder inside RemSound called **user settings and logs**. Each profile is one small file, stored at:


    <RemSound folder>\user settings and logs\profiles\<your computer name>\<profile name>

(If you're upgrading from an older version, RemSound moves all of this into the **user settings and logs** folder automatically the first time you run this version, and tells you once that it's done it. Nothing is lost.) RemSound updates never touch that folder — so anything of your own in there stays safe when you update.

The built-in cue sounds are kept separately, in a **default sounds** folder alongside the program. Those are part of RemSound itself, so an update can refresh them — if a future version ships an improved default sound, you'll get it. Your own choices are never affected: a sound you pick for a cue with the **Browse** button is remembered as a link to your own file (wherever you keep it), and that's left exactly as you set it.

The folder named after your computer keeps each machine's profiles separate. If you used the **Browse …** button on the startup dialog to pick a different folder (for example, one inside Dropbox), the profiles are stored directly in that folder — with no per-computer subfolder — so two computers pointed at the same shared folder see exactly the same list.

You can also **copy a profile file from one computer to another** : drop it into the other computer's profile folder and it will appear in that computer's startup dialog. If the other computer doesn't have the same equipment (different sound cards, different ASIO drivers), those device choices are simply skipped when the profile loads — RemSound won't show an error or a warning, the relevant lists just won't have those items ticked.

> **Tip:** profile files are plain text and readable by people. If you ever want to change something by hand (for example, a remembered peer) without opening the app, you can open the file in any text editor.

### What is NOT saved in a profile

A few things are deliberately kept out of profiles:

  * **The folder profiles are read from.** You choose this with the Browse… button on the startup dialog. It's kept in a small settings file on that particular computer.
  * **Live connection health figures** — these describe what's happening right now, not your setup.
  * **Anything auto-tune has learned** — this is worked out fresh each session.
  * **Window position and size** — Windows itself remembers these.



### Locking a profile (read-only)

By default, RemSound treats your profile like a document: if you change something while it's running, you'll be asked “save changes?” when you exit. Most of the time that's exactly what you want — you don't lose work by accident.

But sometimes you want the opposite. You have a profile you live in every day, you toggle send or receive on or off during the day as a matter of course, and you don't want to be asked about saving every single time you close RemSound. You especially don't want to be asked if RemSound might close itself for some other reason (a Windows update, your screen reader crashing, a remote session dropping, a laptop going into hibernate) — because then there's a save prompt sitting on screen that nobody can dismiss, and the app can't actually close.

**Locking the profile** solves this. When ticked:

  * The profile loads normally and everything in the app works the same way it always did.
  * Anything you change during the session — ticking a device, sliding the volume, toggling send or receive, picking a peer — **still works for that session**. RemSound just doesn't write any of it back to the profile file on disk.
  * When you close RemSound, there is **no save prompt**. The app just closes. Whatever you changed during the session is forgotten; the next time you open the profile it's back to what it was when you locked it.
  * The window title shows “(read-only)” so you can always tell at a glance.
  * The startup profile picker also shows “(read-only)” next to locked profiles, so you know what you're picking before you hit Enter.
  * Pressing Save (Ctrl+S) still works — the lock only blocks the automatic save prompt, not deliberate saves. See “Saving on purpose while a profile is locked” below.



**How to lock or unlock:** open the File menu (Alt+F) and pick **Lock profile (read-only)** (Alt+F, L). It's a tickable menu item — pick it once to turn the lock on (a tick appears next to it); pick it again to turn the lock off (the tick disappears). The lock state is remembered with the profile, so closing and reopening RemSound keeps the profile locked exactly as you left it.

#### Saving on purpose while a profile is locked

The lock is there to stop accidents — it doesn't stop you saving when you mean to. If you press **Save** (Ctrl+S) or pick **File → Save** on a locked profile, RemSound shows a one-time warning explaining what's about to happen:

> **Saving onto a read-only profile.** You're about to save changes onto a profile that's marked as read-only. RemSound allows this because you asked to save on purpose — the lock only stops the automatic “save your changes?” prompt; it doesn't stop you saving when you mean to.
>
> Click **Save anyway** to overwrite this profile, or **Cancel** and use File → Save as… if you'd rather save your changes to a new profile.

There's a **Do not show me this message again** tick on the warning. Once you tick it, future deliberate saves on a locked profile go through silently without the warning. The setting is per-machine, not per-profile — tick it once and it applies on every locked profile from that point on.

So in summary, on a locked profile:

  * Closing RemSound — no prompt, changes are forgotten.
  * Switching to a different profile — no prompt, changes are forgotten.
  * Pressing Save (Ctrl+S) on purpose — warning the first time (with a do-not-show-again tick), then the save goes through and overwrites the profile.
  * **Save as …** — always works, never warns. The new copy starts out unlocked.



> **If a save prompt is blocking your shutdown right now:** close it by pressing Esc (or click Cancel if you can see it), unlock by going File → Lock profile (read-only), then close RemSound. From this launch forward there'll be no prompt.

## 4. The main window: menu bar and tabs

The main window has three parts, stacked top to bottom:

  1. A **menu bar** at the top with four menus — _File_ , _Record_ , _Options_ and _Help_. See Menus.
  2. A **row of tabs** — Connectivity, Audio inputs and outputs, Volume, pan and EQ for peers, and Audio profile — so four by default. The Volume, pan and EQ tab can be hidden, and you can reorder or hide any of the tabs (and jump straight to one with Ctrl and its number) from the **Appearance** tab in Preferences. Each tab has its own Alt+letter shortcuts that only work when that tab is the one showing — so the same letter can do different things on different tabs without clashing.
  3. A **status line** at the bottom that updates once a second with how long you've been connected, how many peers you have, whether sound is flowing, connection health, and RemSound's own CPU and memory usage.

Tab| What it's for
---|---
**Connectivity**|  Connected, discovered and remembered peers. Adding a peer by address. A connection status read-out.
**Audio inputs and outputs**|  The ASIO driver picker (when an ASIO driver is installed), the Receive audio and Send my audio checkboxes, and all the device lists. Choosing a real driver in the picker brings up the ASIO device lists alongside the ordinary Windows ones; choosing _(none)_ hides them.
**Volume, pan and EQ for peers** (optional)| Shape each connected peer's sound on its own — their volume, pan (left/right) and EQ. Shown by default; untick “Show the volume, pan and EQ for peers tab” on the Appearance tab of Preferences to hide it. See Volume, pan and EQ for peers tab.
**Audio profile**|  Codec, packet size, latency, continuous auto-tune, buffer smoothness, artefact sound. Split into an _Audio send parameters_ group and an _Audio receive parameters_ group.

### The system tray icon and its menu

When RemSound is **minimised to the tray** (via **File → Minimise to tray**, the “Show or hide window” global hotkey, or by starting minimised on launch), the main window hides and an icon appears in your Windows system tray (the small icons cluster next to the clock).

**Hovering over the tray icon** shows a short summary of what RemSound is doing right now — the number of healthy peers, whether you're sending or receiving and in which mode (WASAPI, ASIO, or both), and whether a recording is running. The summary keeps itself up to date as things change. It tells you a recording is in progress, but not its exact length — for that, glance at the main window. Examples:

  * _RemSound — not connected_
  * _RemSound — 2 peers, sending (WASAPI), receiving (WASAPI)_
  * _RemSound — recording, 1 peer, sending (WASAPI + ASIO), receiving (WASAPI + ASIO)_



**Right-clicking the tray icon** opens a small menu with everything you might want to reach without re-opening the main window:

Item| Shortcut| What it does
---|---|---
**Show RemSound**|  W| Brings the main window back to the front and gives it focus. Double-clicking the tray icon does the same thing.
**Enable sending** (tickable)| S| Toggles “Send my audio” on or off, the same way as the checkbox on the Audio inputs and outputs tab. The tick reflects the current state — ticked means sending, unticked means not.
**Enable receiving** (tickable)| R| Toggles “Receive audio” on or off. Same tick-reflects-state rule.
**Profiles →**| P| A submenu listing your recent profiles, most recent first. Each row has a single-digit shortcut: while the submenu is open, press **1** for the most recent, **2** for the next, and so on up to **5**. Selecting one switches the active profile, exactly the same way as the File menu's Recent profiles submenu. Greyed out as “(No recent profiles)” when you haven't loaded any yet.
**Exit**|  X| Closes RemSound entirely.

**Keyboard access:** the tray icon is reachable through standard Windows shortcuts — **Windows + B** moves focus to the notification area, arrow keys navigate, Enter activates, and the application context-menu key (or Shift+F10) opens the right-click menu without a mouse.

**Important warnings always come to the front.** Even when RemSound is hidden in the tray, a warning it needs you to read — such as the “your files have moved” notice, a microphone-blocked warning, or an update prompt — pops up in front of whatever you're doing, with focus, so your screen reader reads it straight away. RemSound stays in the tray; only the warning comes forward.

### Only one copy of RemSound runs at a time

RemSound only ever runs as a single copy. If you try to open it while it's already running — for example by double-clicking it when it's already sitting in the system tray — it won't start a second one. Instead it asks what you'd like to do:

  * **Switch to the running copy** — brings the copy that's already running back to the front. This is the default and usually what you want. Remember it may be minimised to the system tray, down by the clock.
  * **Force the running copy to close and start fresh** — only needed if the running copy is stuck or not responding. It closes that copy and starts a new one. If the stuck copy was in the middle of a recording, that recording is lost — so this is the “get me out of trouble” option, not an everyday one.
  * **Cancel** — do nothing.



## 5. Menus (File, Record, Options, Service, Help)

There are four menus on the main window: **File (Alt+F)** , **Record (Alt+K)** , **Options (Alt+O)** and **Help (Alt+H)**. The Record menu opens with Alt+K rather than Alt+R because Alt+R is already used by the **Receive audio** checkbox on the main window. The menu's title is shown as “Record (Alt+K)” so you can find the shortcut even though there is no K in the word.

### File menu

The File menu holds everything to do with profiles — opening, saving, renaming — plus minimising to the tray and exiting.

Item| Shortcut| What it does
---|---|---
**New profile**|  Ctrl+N, or Alt+F, W| Starts a brand-new profile from scratch — a fresh, unsaved session (everything unticked, nothing connected, default settings). This is how you create a profile for a different setup at any time, _even when RemSound is set to start straight into a specific profile and you never see the picker_. If your current profile has unsaved changes, it offers to save them first. Once you've set things up, use Save as to give the new profile a name.
**Open profile …**| Ctrl+O, or Alt+F, O| Opens a Windows file picker showing your profiles folder. Pick a profile, and RemSound reloads using it (the window closes and reopens with all that profile's device choices, peers and settings restored). To delete a profile, right-click its entry inside the file picker and choose Delete — that lets Windows handle the deletion.
**Recent profiles →**| Alt+F, R| A submenu listing the last five profiles you've opened, most recent first. Each row has a single-digit shortcut: while the submenu is open, press **1** for the most recent, **2** for the next, and so on up to **5**. Or just select the one you want. It reloads the profile the same way Open profile does. If a recent profile's file has been deleted or moved away, it's left out of the submenu (it stays in the list in case the file comes back later — for example when you reconnect an external drive). If the list is empty, you see a greyed-out “(No recent profiles)” entry. The same list appears in the system-tray icon's **Profiles** submenu, with the same number shortcuts, so you can switch profiles without re-opening the main window.
**Save**|  Ctrl+S| Updates the current profile with your current settings. If there's no current profile (you're on a new profile), this becomes Save as automatically.
**Save as …**| Alt+F, A| Asks for a name and saves a copy. Use it to save your current setup under a new name, or to save for the first time from a new profile.
**Rename current profile …**| Alt+F, M| Renames the current profile's file and updates the window title. Does nothing on a new profile (there's no profile to rename).
**Lock profile (read-only)** (tickable)| Alt+F, L| When ticked, the current profile is loaded for use but RemSound will not save any of your changes back to it. The window title shows “(read-only)” so you can tell at a glance. Save (Ctrl+S) politely refuses with a hint to use Save as instead, and closing RemSound never asks “save changes?” — it just closes. Anything you've changed during the session is forgotten when RemSound closes; the file on disk is left exactly as it was. The lock setting is saved on the profile itself, so it sticks across launches. See Locking a profile for the full story.
**Minimise to tray**|  Alt+F, N| Hides the window down to the system tray (the small icons near the clock). The tray icon's hover summary tells you what RemSound is doing, and right-clicking it gives you Show RemSound, Enable sending, Enable receiving, your Profiles submenu, and Exit — see The system tray icon and its menu for the full rundown. To bring the window back, double-click the tray icon, pick “Show RemSound” from its menu, or use the “Show or hide window” global hotkey (set in the Keyboard shortcuts dialog, default Ctrl+Shift+F10).
**Exit**|  Alt+F, X (or Alt+F4)| Closes RemSound. If you have unsaved profile changes (and the profile isn't locked), it asks you first.

### Record menu

The recording feature can save what you're sending, what you're receiving, or both, to a file on your computer as a WAV, MP3, OGG-Opus or FLAC file. See Recording to a file for the full chapter; this is just the menu summary.

Item| Shortcut| What it does
---|---|---
**Start recording / Stop recording**|  Ctrl+R, or Alt+K, R| A toggle. The label switches between “Start recording” and “Stop recording” to show whichever action the next press would do. Either label is activated by the letter R. Each time you start, RemSound plays a short cue sound (if you've enabled it in Preferences), then creates a new recording in your recordings folder, named by date and time (see Recording to a file). Stopping closes the file and plays the stop cue. Ctrl+R works from anywhere in the main window.
**Open current recordings folder**|  Alt+K, O| Opens your recordings folder in Windows File Explorer. It creates the folder if it doesn't exist yet (which happens the first time on a fresh install).
**Change recordings folder …**| Alt+K, C| A folder picker. Choose a different folder for future recordings. The choice is saved in the current profile, so different profiles can record to different places.

### Options menu

The Options menu gathers everything you might want to configure about the app — recording settings, keyboard shortcuts, profile passwords, and general preferences.

Item| Shortcut| What it does
---|---|---
**Recording settings …**| Alt+O, S| Opens the Recording settings dialog. Up to five lists: _Recording source_ (Alt+S), _File format_ (Alt+F), _Audio format attributes_ (Alt+A), _FLAC compression level_ (Alt+L — only shown when FLAC is chosen), and _Channels_ (Alt+C) — plus two tickboxes: _Split recording into separate tracks_ (one file per peer) and _Bypass pan and EQ when recording_ (record the raw audio). The attributes list changes to match the format you pick. OK saves to the current profile; Cancel discards.
**Keyboard shortcuts …**| Ctrl+K, or Alt+O, K| Opens the global hotkey dialog (mute, volume, show/hide window, start/stop recording, remote-control commands, speak the status line).
**Profile passwords …**| Alt+O, W| Lists every profile alongside its password, so you can view or change any of them in one place.
**Manage named peers …**| Alt+O, N| Opens a list of every peer you've given a friendly name to, showing each one's machine name and where and when you last connected to it. Pick one and **Rename (Alt+R** , or F2) to change its name, or **Delete (Alt+D** , or the Del key) to forget it — deleting only drops the name, the peer still connects as normal under its machine name. See the peer-naming notes on the Connectivity tab.
**Enable / Disable Realtek ASIO**|  —| Only shown if a Realtek ASIO driver is installed. Lets you reverse the choice RemSound offered about disabling that driver (Realtek's generic ASIO driver tends to grab the wrong device and clash with your screen reader).
**Install RemSound on this PC …** (or **Uninstall …**)| Alt+O, I (Alt+O, U once installed)| Turns the copy you're running into a properly installed Windows app — on the Start menu, with a desktop shortcut, and listed in Windows’ Installed apps — or removes it again. Once it's installed, this item changes to **Uninstall RemSound from this PC**. See Installing RemSound on your PC.
**Preferences …**| Ctrl+P, or Alt+O, P| Opens the Preferences dialog, organised into six tabs (move between them with Ctrl+Tab, or the arrow keys when the tab names have focus): **General** — the profiles folder, accept remote volume commands, UPnP router opening, and two buttons to clear the shared address books: **Clear remembered peers list (Alt+P)** and **Clear remembered applications list (Alt+L)** (each asks you to confirm first; these lists are shared across all your profiles, so clearing empties them everywhere); **Appearance** — **Colour theme (Alt+T)** (Match Windows / Light / Dark; changes only how the window looks, takes effect next launch, no effect on the screen reader), “Show the volume, pan and EQ for peers tab” (on by default; hides the Volume, pan and EQ for peers tab if you untick it), **Tab order (Alt+O)** — a list of the window's tabs with **Move up (Alt+U)** and **Move down (Alt+N)** buttons to reorder them, and two toggles to **enable the Discovered (Alt+D)** and **Remembered (Alt+R)** peer lists on the Connectivity tab (both on by default — untick one to hide that list); **Audio cues** — the cue list and its sounds (see Audio cue sounds); **Startup behaviour** — start minimised / with Windows / with a specific profile; **Update settings** — the update checks and install options; and **Logging** — enable logs, write logs now, and the log-folder housekeeping (see Logs and diagnostics). Esc or the Close button dismisses it.

### Installing RemSound on your PC

RemSound normally runs “portable” — you unzip it and run it straight from whatever folder you unzipped it to, and it keeps its settings, profiles and recordings inside that same folder. That works perfectly well and you never have to install anything. But if you'd rather have RemSound set up as a proper Windows app — on the Start menu, with a desktop shortcut, and listed in Windows’ _Installed apps_ — open **Options → Install RemSound on this PC** (Alt+O, I).

It installs into your own user area (`…\AppData\Local\Programs\RemSound`), so it **never asks for administrator rights** and only affects your account. A dialog lets you choose what to set up. Tab through the tick-boxes, then select **Install** (pressing Enter on a tick-box won't skip ahead — you reach the Install button by tabbing to it):

  * **Create a desktop shortcut** — on by default.
  * **Add to the Start menu** — makes a “RemSound” folder on the Start menu holding three shortcuts: the program, this manual, and an uninstall shortcut. On by default.
  * **Run RemSound when I sign in to Windows** — the same login auto-start as the Startup behaviour tab. Off unless you already had it turned on.
  * **Copy my profiles and settings across** — brings your profiles and every setting (keyboard shortcuts, named peers, colour theme, and the rest) from the copy you're running into the install folder. On by default.
  * **Copy my recordings across** — on by default.
  * **Copy my logs across** — off by default; you rarely need old logs in the installed copy.



When you select Install, RemSound copies itself into place and then **closes and reopens from the installed location**. A confirmation dialog tells you this is about to happen before it does. From then on your settings, profiles and recordings live inside the installed folder, exactly as they did in the portable copy, and RemSound still updates itself in place as usual.

**Uninstalling.** Once RemSound is installed, that same menu item becomes **Uninstall RemSound from this PC** (Alt+O, U). You can also uninstall from the “Uninstall RemSound” shortcut in its Start-menu folder, or from Windows’ _Installed apps_ list — all three do exactly the same thing. RemSound asks you to confirm first, with two tick-boxes — **Remove profiles, config and logs** and **Remove recordings** — both **off by default** , so unless you tick them your own files are kept even after the program itself is removed. A short message confirms once it's done.

### Help menu

The Help menu opens this manual, checks for updates, and shows the About dialog.

Item| Shortcut| What it does
---|---|---
**Help**|  F1 (anywhere), or Alt+H, H| Opens this user manual in your default web browser. F1 also works from inside every dialog (Preferences, Keyboard shortcuts, About, Startup behaviour) and from the startup profile picker, before the main window has even loaded.
**Check for updates**|  Alt+H, C| Asks the RemSound website whether a newer version is available. If there is one, you get a confirmation dialog with the release notes and a Yes / No to install. If you're already up to date, a popup tells you so. (To have RemSound check on its own instead of pressing this button, see Updating RemSound.)
**About RemSound**|  Alt+H, A| A small dialog showing the version you're running and the latest release notes in a scrollable read-only box. Close (or Esc) dismisses it.

### The Appearance tab (Preferences)

The **Appearance** tab of Preferences (Options → Preferences, or Ctrl+P) controls how the window looks and is laid out. Nothing here affects the sound or the screen reader. Changes take effect as soon as you close Preferences — except the colour theme, which applies the next time you start RemSound.

Control| Shortcut| What it does
---|---|---
**Colour theme**|  Alt+T|  _Match Windows_ (the default), _Light_ , or _Dark_. RemSound follows your Windows light/dark setting unless you pick a fixed one.
**Show the volume, pan and EQ for peers tab**|  Alt+Q| On by default. Untick to hide the Volume, pan and EQ for peers tab from the main window.
**Tab order**|  Alt+O| A list of the main window's tabs. Pick one, then use **Move up (Alt+U)** and **Move down (Alt+N)** to change the order the tabs appear in — which also sets their Ctrl+number (Ctrl+1 is always whichever tab is first). All four tabs are listed even when the volume/pan/EQ tab is hidden.
**Enable the discovered peers list on the Connectivity tab**|  Alt+D| On by default. Untick to hide the Discovered peers list from the Connectivity tab.
**Enable the remembered peers list on the Connectivity tab**|  Alt+R| On by default. Untick to hide the Remembered peers list from the Connectivity tab.

The **Service** menu (Alt+S) installs and controls the optional lock-screen service that keeps sending your audio when you are not at the machine. See that section for the details.

## 6. Connectivity tab

This is where you manage peers and reach the logging options. The controls on this tab, in tab order:

Control| Shortcut| What it does
---|---|---
**Connected peers**|  Alt+C| The people you currently have sound flowing with. Unticking a row disconnects that peer.
**Peer details**|  Alt+E| A read-only box describing whichever connected peer you're on in the list above. Arrow through it to read: their name, their machine name, their IP address, how long you've been connected, the link health and ping, what they're sending (how many devices, on WASAPI or ASIO, at what sample rate and codec), and whether they're receiving your audio. The device and WASAPI/ASIO detail only shows while you're actually receiving that peer.
**Rename peer**|  Alt+M or F2| Give the highlighted peer a friendly name of your choosing. It opens a box with the name and a **Clear custom name** button. The name sticks to that machine for good — across restarts, IP changes and networks — and shows everywhere that peer appears: the lists here, the volume, pan and EQ for peers tab, the status line and split-recording filenames. See below.
**Discovered peers**|  Alt+D| People RemSound has heard from in the last few seconds. Tick someone to connect to them.
**Remembered peers**|  Alt+R| People you've connected to before, or added by address. This list is kept between sessions. Tick someone to reconnect. Press **Delete** on an entry to forget it.
**Add peer by IP**|  Alt+A| Opens a small box where you type an address or computer name. It adds that peer to the remembered list and connects.
**Lock to these exact peer addresses**|  Alt+L| When ticked, this profile uses only the exact addresses you set and never follows the other computer by name or switches to a different address — even if the address stops working. Off by default, saved with the profile. See Locking a profile to one exact address.
**Connection status**|  Alt+S| A read-only box of text that sums up everything happening right now — how long you've been connected, how many peers you have, how much sound is flowing each way, and the connection health of each peer. Open it to read the current connection status.

The **Discovered** and **Remembered** peer lists can each be hidden if you don't use them — untick them on the Appearance tab of Preferences. Hiding one just removes it from this tab; it changes nothing about how you connect.

### Giving a peer a friendly name

Highlight a peer in the **Connected peers** list and press **Rename peer (Alt+M)** to call them whatever you like — “Andre's desktop” instead of “ANDRE-DESKTOP”. The name is tied to that _machine_ , not to its address, so it survives their restarting RemSound, their address changing, and their reaching you on a different network (local network one day, Tailscale the next). Once set, it replaces the machine name everywhere that peer shows up — both peer lists, the volume, pan and EQ for peers list, the connection status, and the per-peer files a split recording makes.

In the rename box, type the name and press OK, or press **Clear custom name (Alt+C)** to drop back to the machine name. (Leaving the box empty and pressing OK does the same.) The names are kept per machine you're using RemSound on, and apply in every profile. One case to know about: a peer you added purely by address that never announces a name has no machine name to pin to, so its friendly name is tied to the address instead and would need re-setting if that address changes.

To see and tidy up all your named peers in one place — including ones that are currently offline — use **Options → Manage named peers**. It lists each one with its machine name and when you last connected, and lets you rename or delete any of them.

## 7. Audio inputs and outputs tab

This tab controls everything to do with which sound devices are involved. The ASIO driver picker at the top decides whether ASIO is being used at all. The Receive side and the Send side each have their own master checkbox and their own device lists.

Control| Shortcut| What it does
---|---|---
**ASIO driver**|  Alt+D| A list that starts with _(none)_. Pick _(none)_ and the app uses the ordinary Windows sound path only; pick a real driver and the ASIO device lists appear below, and the Audio profile tab gains a second delay setting. If your computer has no ASIO drivers installed, this control is hidden completely.
**Receive audio**|  Alt+R| The master switch for receiving. When it's off, no sound plays out, no matter which output devices are ticked.
**WASAPI outputs for received audio**|  Alt+3| Tick which ordinary Windows outputs (speakers, headsets) should play the received sound. Ticking more than one means the received sound plays out of all of them at once.
**ASIO outputs for received audio**|  Alt+1| (Shown when an ASIO driver is chosen.) Tick which ASIO channel pairs should play the received sound.
**Master volume for received audio**|  Alt+V| A slider: the master volume for everything coming in. There is no separate volume per device here; per-person volume lives on the Volume, pan and EQ for peers tab.
**Send my audio**|  Alt+S| The master switch for sending.
**WASAPI audio outputs to send**|  Alt+4| Tick which Windows output devices to capture from — this captures whatever is currently playing on those speakers and sends it.
**WASAPI audio inputs to send**|  Alt+5| Tick which Windows input devices to capture (microphones, line-ins).
**ASIO audio inputs to send**|  Alt+2| (Shown when an ASIO driver is chosen.) Tick which ASIO channel pairs to capture and send.

All the device lists are checkable lists — tick or untick an item to include or exclude that device. Profiles save which devices are ticked; a new profile starts with everything unticked.

### Following the Windows default audio device

At the very top of the **WASAPI outputs for received audio** list, the **WASAPI audio outputs to send** list and the **WASAPI audio inputs to send** list there's a special entry: **Use Windows default audio device, follows Windows changes**. Tick it and RemSound uses whatever Windows currently treats as the default — the default speakers for received sound, the default output for what you send, the default microphone for input — and, the useful part, it **follows that default on its own**. Make a headset your default and RemSound switches to it; unplug it and RemSound moves back, all without you touching the list.

Ticking it is **exclusive** : RemSound unticks every specific card in that list and locks them so they can't be re-ticked while the default entry is on. That keeps things unambiguous — in each list you're either following the Windows default or picking specific cards, never a mix. Untick the default entry and the specific cards become available again.

Unlike the specific device ticks (which start fresh each session), the “use default audio device” choice is remembered between launches. It's safe to remember because it can never point at the wrong card — it always resolves to whatever Windows is using right now.

### Receiving

To receive sound you need two things: **Receive audio** ticked, and at least one output device ticked. Without an output device, even when sound arrives there is nowhere for it to go.

Tick as many outputs as you like across the WASAPI and ASIO output lists — the same received sound plays out of all of them. Common combinations:

  * One output: just your monitors or headphones.
  * Studio monitors through ASIO plus a wireless headset through WASAPI, so you can move around the house.
  * Two physical outputs, one for each of two rooms.



**Hearing everyone with one output ticked.** A peer always tells you which sound path it's sending from. On your end, if you only have one type of output device ticked, sound from a peer using the other type is still routed through whatever output you do have ticked. So a single ticked output is enough to hear everyone.

### Sending

To send sound you need **Send my audio** ticked, plus at least one capture source ticked across the three send lists.

List| What it captures| Typical use
---|---|---
WASAPI audio outputs to send| Whatever Windows is currently playing through that output. So picking your “Speakers” device captures whatever you're hearing.| Sharing music playback, sharing the sound from a video call, anything coming out of your own speakers.
WASAPI audio inputs to send| Sound captured straight from a microphone or line input.| Your USB microphone, a headset mic, a line-in.
ASIO audio inputs to send| An ASIO channel pair — usually a hardware input on a professional audio interface.| An instrument input on an Audient EVO, a microphone preamp on a Focusrite, and so on.

Tick any combination across the three lists. RemSound mixes them together into one stream and sends that to all your chosen peers. So you can send a mic plus a guitar plus your system sound all at once, mixed together, and your friends hear all three.

> **Capturing your speakers can cause an echo loop.** If you tick the same device both in “WASAPI audio outputs to send” and in “WASAPI outputs for received audio”, then the received sound plays out of that device, gets captured again, and gets sent back. The other person ends up hearing their own voice on a delay. Don't tick the same device on both sides at once.

> **If your microphone sends silence:** Windows can block desktop apps from using the microphone, and when it does, RemSound's mic capture still switches on but only sends silence — so you look like you're sending, but the other person hears nothing. RemSound watches for this: when you tick a microphone in **WASAPI audio inputs to send** while Windows is blocking it — or load a profile that already has one ticked — a message pops up telling you, with the exact two settings to turn on — open Windows Settings → Privacy & security → Microphone, then turn on both _Microphone access_ and _Let desktop apps access your microphone_. The check also catches the sneakier kinds of block: one aimed at RemSound alone in that same Settings page's per-app list, and one set by an administrator or workplace policy — that last kind doesn't show up as a switch you can flip, so if the warning says a policy is involved, it needs whoever manages the computer to lift it. (ASIO inputs aren't affected, because ASIO talks straight to the hardware and bypasses that Windows privacy gate.) It doesn't change anything you receive — only sending your own mic.

### Sending specific applications instead of whole devices

By default the WASAPI output list sends _everything_ playing on a sound device. If you'd rather send only **one particular program** — say foobar2000 or a browser — and nothing else, use the **How to send WASAPI audio (Alt+6)** chooser, just below _Send my audio_. It has two settings:

  * **Send whole audio devices** (the default) — the ordinary behaviour, using the “WASAPI audio outputs to send” list described above.
  * **Send specific applications** — swaps that device list for two application lists. (This needs Windows 10 version 2004 or newer; on older Windows the chooser is hidden and only whole-device sending is available.)



When you choose _Send specific applications_ , two lists appear:

List| What it holds
---|---
**Currently active applications (Alt+8)**|  Every program making sound right now. Tick one and only that program's audio is captured and sent — its own private stream, separate from everything else on the machine. Tick several to send several. A program you've ticked that isn't running at the moment still shows here marked _(not running)_ , so you can always find it and untick it; it starts being sent again the instant it reopens.
**Remembered applications (Alt+9)**|  Your saved “apps I send” address book — shared across all your profiles, like the remembered peers list. A program joins this book the moment you first _tick_ it in either list — that's the only way in, so after clearing the book it simply refills as you tick apps again. Tick a program here and it moves up to the active list the moment it's running (and is captured from its very first sound). Untick a program in either list and it drops back here. Press **Delete** on an entry to forget it, just like the remembered peers list.

Because sending is by program _name_ , your choice survives that program being closed and reopened, or even the computer restarting. There is deliberately no “send everything” option in applications mode — if you want the whole machine's sound, that's what _Send whole audio devices_ is for.

## 8. Audio profile tab

Everything that shapes the trade-off between sound quality and delay lives here. The first control on the tab is the _priority mode_ checkbox — it sits on its own at the top because it has the biggest single effect on how the audio feels in the first few seconds. Below it are two groups: **Audio send parameters** first, then **Audio receive parameters**.

### Use CPU and Windows performance settings in high priority mode (Alt+U)

This is the first control on the tab. When it's ticked, RemSound asks Windows to keep it running at full speed the whole time RemSound is open under this profile.

The effect is that the “the first few seconds sound rough, then it warms up” behaviour goes away — nothing in the system is allowed to coast while RemSound is sitting quietly between bursts of sound.

This is a **per-profile** setting, so you can have one profile for live sessions where it's on, and another for casual background listening where it stays off. Turning it on or off marks the profile as having unsaved changes; save the profile to keep your choice.

When to tick it| When to leave it off
---|---
Playing music together live. Anything where the first few seconds matter. Professional setups using ASIO at very low delay targets (under 15 ms). Sessions where the computer sits idle between short bursts of sound.| A laptop running on battery, especially for a long session. Background listening for hours at a time. A passive monitoring setup that doesn't need a fast start.

The cost on a desktop is a couple of extra watts while RemSound is open. The cost on a laptop running on battery is that the battery drains a bit faster over the session, because the processor stays more wakeful instead of dozing — RemSound's own workload doesn't change, the processor just doesn't sleep as deeply. The setting is reversed automatically when RemSound closes (or when you untick it), so it's fine to leave the app running with the box ticked for a whole session, and turning it off partway through works too.

None of the other apps on your computer are affected. RemSound only asks Windows to keep _itself_ running at full speed; Windows still saves power on everything else as normal, so your screen reader, browser and background programs are untouched.

### Audio send parameters

Control| Shortcut| What it does
---|---|---
**Audio codec**|  Alt+C| The codec is the method RemSound uses to package the sound before sending it. Three choices: PCM 48k 24-bit (uncompressed), Opus broadcast quality (loss tolerant), or Opus live latency (for jamming and monitoring). See codec choice.
**Packet size**|  Alt+P|  _Standard_ (the default) or _Small_ (for a local network only). Smaller packets save a couple of milliseconds of delay on the sending side, but they double how many packets are sent.

RemSound always **locks its sending timing to the sound device's own hardware clock** — this used to be a “Lock to audio clock” checkbox, but it is now always on, because turning it off only ever added delay. There is nothing to set.

### Audio receive parameters

What you see in this section depends on whether an ASIO driver is chosen on the Audio inputs and outputs tab. With no ASIO driver, you see one delay setting (labelled simply “Audio latency”). With an ASIO driver chosen, you see two delay settings — one for each sound path — each with its own auto-tune toggle. The two paths are independent: a problem on one doesn't affect the other.

Control| Shortcut| What it does
---|---|---
**ASIO latency in milliseconds**|  Alt+L| (Only when an ASIO driver is chosen.) A small up/down number control. It sets the target amount of sound to keep buffered for the ASIO path. Default 10 ms. ASIO can sustain very low values, but going below the network's real-world jitter level (typically 15–25 ms) causes constant tiny corrections that you can hear — pick 25 ms as a safe floor unless both computers are on the same wired network or the same machine.
**Continuous auto-tune ASIO latency**|  Alt+T| (Only when an ASIO driver is chosen.) A checkbox. It nudges the ASIO delay target as the ASIO path's jitter changes. It works independently of the WASAPI toggle.
**WASAPI latency in milliseconds** (called just “Audio latency” when there's no ASIO driver)| Alt+W (Alt+L when no ASIO driver)| A small up/down number control. It sets the target amount of sound to keep buffered for the WASAPI path (or the only path, in WASAPI-only setups). Smaller means less delay but more clicks. Most people want 20–80 ms.
**Continuous auto-tune WASAPI latency** (called “Continuous auto-tune latency” when there's no ASIO driver)| Alt+Y (Alt+T when no ASIO driver)| A checkbox. When it's on, RemSound nudges the WASAPI delay value automatically as the network changes. The companion interval combo box (**Alt+I**) sets how often it re-checks: 3, 5, 10, 15, or 30 seconds. The combo's label is “Auto-tune latency interval” in WASAPI-only setups and “Auto-tune interval — WASAPI and ASIO” when an ASIO driver is chosen, because that one timer drives both paths' auto-tuning. Each path still settles at whatever target its own calculation chooses; only the timing of the re-checks is shared.
**Buffer smoothness**|  Alt+B| A list, 1 to 10. It controls how patient the receiving side is with sound that arrives late, on either path. Higher means more protection from clicks but a longer steady delay. Default 3.
**Artefact sound type**|  Alt+A| A list. _Noise burst_ (the default) fills a momentary gap with a brief soft hiss, which blends into music. _Click_ leaves the gap unfilled so you hear an obvious click — useful when you want to hear every problem.

Most people only need to pick a codec and a smoothness level, and leave everything else at its default.

## 9. Volume, pan and EQ for peers tab

This tab lets you shape the sound of each peer you're connected to. You can set how loud that person is, lean them to the left or right, and change their tone with an equaliser. It's handy when you have several people connected at once and want to mix them — for a jam session you might put the drummer over to the left, turn someone down a little, or brighten someone up.

The tab is **shown by default**. If you don't want it, untick **“ Show the volume, pan and EQ for peers tab”** on the **Appearance** tab of Preferences (Options → Preferences, or Ctrl+P). When it's on, the tab appears just before the Audio profile tab.

### Turning it on, and choosing who to shape

There's a single master switch, then a list of the people you're connected to. Tick a person in the list to shape them; whoever your cursor is on in the list is the person the controls below are editing. So you arrow to someone, tab down, and their volume, pan and EQ are right there. Unticking a person leaves their settings intact but passes their sound through untouched — a quick per-person bypass. The tab, from top to bottom:

Control| What it does
---|---
**Enable volume, pan and EQ for all peers (Alt+E)** (checkbox)| The one master switch. When it's off, everyone passes through untouched — but you can still set everything up ready for when you turn it on. There's also a global keyboard shortcut to flip this switch from anywhere (you set the key yourself in Keyboard shortcuts — it starts unset).
**Peers (Alt+U)** (checklist)| The people you're currently connected to. Tick the ones you want shaped; untick to bypass a person while keeping their settings. Move your cursor onto a person to edit them — everything below acts on whoever the cursor is on.
**Volume (Alt+L)** (slider)| An individual level for that one peer, from 0 to 100% (100% means unchanged). It sits on top of your main volume, so you can balance people against each other.
**Pan (Alt+N)** (slider)| Leans the peer to the left or right. Centred by default. It keeps the peer's stereo sound — it never folds them down to mono.
**Set peer EQ to default (Alt+Q)** (button)| Puts that peer's EQ back to flat — all three modes at once (the 3-band, the 12-band and the parametric bands). It leaves the pan and volume alone.
**EQ mode (Alt+M)** (picker)| Three choices: _3 band simple EQ_ , _12 band advanced graphic EQ_ or _16 band parametric EQ_. This chooses which EQ controls you see below.
**EQ controls**|  For the two graphic modes, a set of sliders (see below). For the parametric mode, an Add band button and a list of your bands. Details follow.

### The three EQ modes

**3 band simple EQ** and **12 band advanced graphic EQ** are graphic equalisers: a set of sliders at fixed frequencies, each running from −12 dB to +12 dB with flat (no change) in the middle. The 3-band has **Bass, Mids, Treble**. The 12-band has **31 Hz, 63 Hz, 80 Hz, 125 Hz, 250 Hz, 500 Hz, 1 kHz, 2 kHz, 4 kHz, 6 kHz, 8 kHz** and **16 kHz**. Each slider reads its level out in words, for example “plus 3 dB”, “minus 6 dB” or “flat”.

**16 band parametric EQ** lets you place your own bands wherever you want them, up to sixteen. Instead of fixed sliders you build a list of bands:

  * Tab past the mode picker to the **Add band** button and press it. A small dialog opens with three boxes: a **start frequency** , an **end frequency** and a **gain in dB** (from −12 to +12). Each box you can type into or spin with the arrow keys; they only accept sensible numbers. As you change the values you hear the band on that peer straight away. Press **OK** to add it, or **Cancel** / **Escape** to drop it.
  * Back on the tab, tab to the **Bands** list to hear your bands, one per row, each read out as its range and level — for example “200 Hz to 800 Hz, plus 3 dB”. The list is sorted low to high, so the bass bands are at the top and the treble at the bottom. Up and down arrow move between bands; **left and right arrow nudge the selected band's gain down or up by half a dB** , so you can fine-tune it on the fly and hear the change straight away.
  * To remove a band, land on it and press **Delete** , or use the **Delete band** button. You can select several at once (hold Shift and arrow, or hold Ctrl and arrow then Space to pick out individual ones) and delete them together.



Each parametric band is a boost or cut spread across the range between its start and end frequencies. A wide range affects a broad sweep of the sound; a narrow one is more surgical.

The three modes are kept separate. Switching between them keeps each one's own settings — nothing carries across from one to another. Only the mode you've picked is the one you hear.

Everything here updates in **real time** — you hear the change as you move a control — and it adds no extra delay to the audio. All of it (the master switch's setting is saved, and each peer's tick, volume, pan and EQ) is stored with the profile. The one exception is the global “toggle everything” keyboard shortcut, which is machine-wide rather than per-profile. There's also a small EQ response graph on the tab; it's purely a visual picture of the shape you've dialled in and plays no part in how you use the tab with a screen reader.

## 10. ASIO and WASAPI

RemSound can use two different ways of handling sound. Which one it uses depends on the **ASIO driver (Alt+D)** list at the top of the Audio inputs and outputs tab.

### WASAPI (the default)

WASAPI is the normal Windows way of handling sound — every speaker and microphone in your Windows sound settings works this way. The delay added by capturing or playing through WASAPI is usually 10–30 milliseconds. Everyone running RemSound has WASAPI; no special equipment is needed.

### ASIO (needs a driver)

ASIO is a faster, more direct way of handling sound used by professional audio equipment. ASIO drivers talk straight to the hardware, giving a hardware delay of under 5 milliseconds. It only works if your audio interface came with an ASIO driver.

The ASIO driver picker doesn't appear at all on a computer with no ASIO drivers installed. Common drivers that _do_ appear:

  * **Audient USB Audio ASIO Driver** — for EVO 4 / 8 / 16 and iD-series interfaces.
  * **Komplete Audio ASIO Driver** — for Native Instruments interfaces.
  * **Focusrite USB ASIO** — for Scarlett, Clarett and Red.
  * **RME ASIO** — for Babyface, Fireface and UCX.
  * **Realtek ASIO** — bundled with some Realtek drivers. _Best avoided_ — it's a generic driver that can grab whatever Windows considers the default device, which often clashes with your screen reader's sound.



### How the driver picker decides

One control, two outcomes:

ASIO driver choice| What happens| Delay
---|---|---
_(none)_|  WASAPI captures and plays the sound directly. ASIO is not used at all.| About 10–30 ms. The lowest possible for anyone without an ASIO driver.
Any real driver name| WASAPI and ASIO both run, side by side, as two independent streams. Each keeps its own native delay — ASIO stays under 5 ms even while WASAPI is also running.| WASAPI at its rate, ASIO at its rate. Each one has its own delay setting on the Audio profile tab (see Latency).

On a fresh install the choice is _(none)_. If you have an ASIO driver and want to use it, select it in the picker. To go back to WASAPI only, select _(none)_.

### ASIO channel pairs

ASIO doesn't list “devices” the way Windows does. Instead it gives you a list of channels (usually 2, 4, 6, 8 or more, depending on the interface), grouped into stereo pairs. RemSound labels each pair with the driver name, the pair number, and the channel names the driver itself reports. For an Audient EVO 8 you'd see entries like:


    Audient USB Audio ASIO Driver — Pair 1 (channels 1/2): Mic | Line | Instrument 1 / Mic | Line 2
    Audient USB Audio ASIO Driver — Pair 2 (channels 3/4): Mic | Line 3 / Mic | Line 4
    Audient USB Audio ASIO Driver — Pair 3 (channels 5/6): Loop-back 1 (L) / Loop-back 2 (R)


### Buffer size for ASIO

RemSound has no buffer-size control of its own. To change the ASIO buffer size, open the control panel program that came with your audio interface (such as NI's Komplete Audio Control Panel or the Audient EVO software) and set it there. The driver remembers its buffer size between sessions; RemSound simply uses whatever the driver is set to.

> **About Realtek ASIO:** if you see “Realtek ASIO” in the driver list, be careful with it. Despite the name, it isn't tied to Realtek hardware — it's a generic driver that opens whatever Windows treats as the default sound device. On a computer that has a real audio interface (Audient, Komplete, and so on), choosing Realtek ASIO will often grab _that_ interface and end up fighting both your real ASIO driver and your screen reader for the same hardware. It's usually best to ignore Realtek ASIO completely.

> **RemSound watches for it for you (new in v3.4):** if a Realtek ASIO driver is installed, RemSound spots it on startup and offers, just once, to disable it — partly for the device-grabbing reason above, and partly because it leaks Windows resources every time it's opened. Say yes and RemSound adds it to a never-touch list and takes it out of the driver picker, so it can't be chosen by accident. You can reverse that — or disable it later if you kept it — any time from **Options → Enable / Disable Realtek ASIO driver in RemSound**. Once you've answered the startup question, RemSound won't ask again.

### Same driver, sending and receiving, on one computer

RemSound supports this — you can capture from your audio interface and play received sound out of the same interface at the same time, on the same computer. Most modern professional audio drivers handle this fine.

## 11. Peers — finding and connecting

A “peer” is another computer running RemSound that you want to talk to. You manage peers on the **Connectivity** tab. It has three lists, all of them checkable:

List| Contents| What ticking does
---|---|---
**Connected peers**|  People you currently have sound flowing with.| Unticking disconnects.
**Discovered peers**|  People RemSound has heard from in the last few seconds — either from an announcement sent across your local network, or from a direct announcement (which is how it works over Tailscale and other VPNs).| Connects you to that peer. Sound starts flowing both ways.
**Remembered peers**|  People you've connected to before, plus any addresses you've typed in by hand. This list is kept between sessions.| Connects to that remembered peer if they're online (and adds them as a manual connection if discovery hasn't found them yet).

There's also the **Add peer by IP (Alt+A)** button, which opens a small box for a computer name or address. It's useful for a first connection over a VPN, where discovery hasn't reached the other computer yet.

### Locking a profile to one exact address (and only that one)

There are two ways to reach another computer, and the difference matters if that computer has more than one address:

  * **By name** — ticking someone in **Discovered peers**. RemSound found them from their announcement, and the entry shows their computer name. This is the easy, automatic way on an ordinary network.
  * **By a fixed address** — **Add peer by IP (Alt+A)** , then type the exact address, for example `10.8.0.1`.



Normally RemSound is helpful about addresses, even one you typed: it can recognise that the address belongs to a computer it also hears announcing itself on the network, and if that computer turns up at a _different_ address — a new IP after a reboot, or a second address over a VPN — RemSound follows it there automatically so your sound keeps flowing. Most of the time that is exactly what you want.

Sometimes you want the opposite: connect to one specific address and nothing else, _ever_. For that, tick **Lock to these exact peer addresses, no matter what (Alt+L)** on the Connectivity tab. It is off by default and is saved with the profile, so you can lock one profile down while another keeps the automatic behaviour. When it is ticked, for that profile:

  * RemSound uses **only the exact address you set** for each peer.
  * It will **not** look the other computer up by the name it advertises on the network, and it will **not** switch to any other address it discovers — even if that same computer is reachable at a second address at the same moment.
  * If the address you set stops working — the computer is off, the network is down, or it has moved to a new address — the connection simply **waits, or drops, until that exact address is reachable again**. RemSound will not go looking for another way through.



**When you use this, set your peers by their IP address** (Add peer by IP, then type the address). That is the setting for a machine that appears under two addresses when you only ever want one of them: add that one IP, tick the box, and save the profile — the connection will use that address and never wander.

With the box left unticked, RemSound behaves as it always has: the profile remembers exactly what you ticked, reconnects by that name or address next time, and quietly follows a peer that genuinely moves to a new address.

### You only hear peers you've ticked

Even if a peer is sending sound your way, you won't hear it until you've ticked their checkbox. This is deliberate — connecting is a step where you give your consent. A peer's name appears in Discovered the moment they come online, but they can't make any sound on your speakers until you say yes.

### Connection health

For each connected peer, the status read-out at the bottom of the window shows a small health note: the latest round-trip time in milliseconds, or **pending** , **stale** or **unreachable** if the regular check-in messages have stopped. (Round-trip time is how long sound takes to travel to the other computer and back.) RemSound plays a connect cue (a short sound) when a peer becomes healthy and a disconnect cue when one becomes unreachable. You can silence both cues using **Audio cue sounds** in the Preferences dialog (Options → Preferences, or Ctrl+P).

## 12. How the network works (LAN, WAN, Tailscale)

RemSound communicates on two network channels:

Channel| Purpose| Default
---|---|---
Audio| The actual sound, sent straight from one computer to the other. The regular health check-ins use this same channel too — one channel, one firewall rule.| 47830
Discovery| “I'm here” announcements every 1.5 seconds, so peers can find each other.| 47831

One audio channel number is used for everything — Tailscale, local network connections, and any relay server. You never need to type a channel number after an address; the default is assumed. Both sides of a connection do need to use the same audio channel number.

The health check-ins travel on the same channel as the audio, so if your sound reaches the other computer, your check-ins do too — one firewall rule covers both.

### Network priority

RemSound automatically asks Windows to treat its audio as high-priority traffic, which helps most on a busy Wi-Fi network where other devices are streaming, downloading or video-calling. There's nothing to set up — it happens on its own every time RemSound starts. This helps on your local network and your home Wi-Fi; it makes no difference once the traffic leaves your home, but it does no harm either.

### LAN — same Wi-Fi or Ethernet

On a normal home network, finding peers and checking their health both work with no setup. Start RemSound on two computers and they'll see each other within a second or two. You usually don't need to change any firewall settings.

### WAN — computers in different places

Connecting two computers directly across the internet needs one of these:

  * A VPN that puts both computers on the same private network — **Tailscale** is the one we recommend. (A VPN is a service that creates a private network linking your computers wherever they are.) Each computer gets a Tailscale address (it looks like `100.something`) and they can reach each other directly.
  * Or, let RemSound ask your router to open the audio port for you automatically — see Automatic router port opening (UPnP) below. Off by default; one tick to turn it on.
  * Or, port forwarding on each end's router by hand (this is more involved and isn't covered here).



### Automatic router port opening (UPnP)

Most home routers support a feature called UPnP (or its newer cousins NAT-PMP and PCP) which lets an app politely ask the router to open a port so the outside world can reach it. RemSound can use this so two computers can find each other across the internet without you having to log into the router and set up port forwarding by hand.

**How to turn it on.** Open **Options → Preferences** (Ctrl+P) and tick **Automatically open my router for incoming connections (UPnP)** (Alt+O). Off by default — we don't want to poke your router without permission. As soon as you tick the box, a status line appears just below it telling you what happened:

Status line says…| What it means| What to do
---|---|---
“Searching for a router that supports UPnP / NAT-PMP / PCP…”| RemSound is asking around on your network for a router that speaks one of these languages. Usually finishes within a few seconds.| Wait a moment.
“Router port opened. Peers can reach you at X.X.X.X:47830.”| Your router has agreed to forward incoming audio to this computer. Tell the peer at the other end that address and they can connect using _Add peer by IP_.| Pass that address (the part before the colon) to whoever you want to connect to.
“No router with UPnP / NAT-PMP / PCP found.”| Either your router doesn't support it, the feature is turned off in the router's settings, or something on your network is blocking it.| Try turning UPnP on in your router's settings page (look for “UPnP” or “NAT-PMP”), or use Tailscale instead.
“The router opened the port, but the external address is on a carrier-grade NAT.”| Your router did its part, but your internet provider has put you behind a second layer of NAT (a sort of giant shared router) and there's nothing your home router can do about that. This is common on mobile broadband and on some cable connections.| Use Tailscale or the relay server instead — both work fine through carrier-grade NAT.
“The router rejected the port-mapping request.”| The router found the request but said no — usually because another device on your network already has the same port forwarded, or because the router has UPnP set to a restrictive mode.| Check your router's UPnP settings, or fall back to manual port forwarding or Tailscale.

**Across sleep and reboots.** If your computer goes to sleep, RemSound asks the router to reopen the port automatically when it wakes up — some routers drop their port-forwarding list during long idle periods. Closing RemSound politely tells the router to forget the forwarding rule, so the port doesn't stay open after you're done.

**Why this is off by default.** Some networks — corporate offices, shared accommodation, hotel Wi-Fi — really don't want apps asking the router to open ports for them, either because there's a security policy or because the router is locked down. Off by default means RemSound never touches your router unless you explicitly tick the box.

### Finding peers on Tailscale and other VPNs

The ordinary “I'm here” announcements that work on a home network don't travel across a VPN. RemSound works around this by also sending announcements directly to every address in your Remembered peers list. So:

  1. One time only: each side adds the other's Tailscale address to its Remembered peers list (using the “Add peer by IP” button).
  2. From then on, RemSound sends announcements straight to those addresses every 1.5 seconds.
  3. The other side hears the announcement, adds the sender to its own list, and announces back.
  4. Within seconds, both sides see each other in Discovered peers, with no further typing.



So the rule is: **only one side has to type the other's address once.** After that, the discovery works both ways on its own.

### Round-trip time and what it means

Round-trip time is how long it takes for sound to travel to the other computer and back.

Round-trip time| What you'll experience
---|---
0–2 ms| The same computer talking to itself.
2–10 ms| Same local network. Effectively instant.
15–40 ms| Typical for Tailscale or modern broadband-to-broadband. Comfortable for conversation.
50–100 ms| Tailscale via a relay, or one end on Wi-Fi a long way off. Still usable, but you start to notice it for music.
100 ms+| Something is wrong, or you're talking across the world. Playing music together is hard.

## 13. Passwords and encryption

From v3.3, **all the audio RemSound sends is encrypted** — scrambled as it leaves your computer and only unscrambled at the other end. Anyone in between (your internet provider, a shared Wi-Fi, anyone watching the connection) just sees noise. This means you no longer need a VPN simply to keep your audio private. And it adds no delay you could ever notice — the scrambling happens in millionths of a second, far less time than the audio itself takes.

### How it works: a password per profile

Every profile carries a **password** , and that password is the key. The rule is simple:

  * **Same password on both ends →** you connect and hear each other.
  * **Different passwords →** no audio passes, and RemSound tells you so (see below) rather than leaving you with mysterious silence.



So the password does double duty: it both encrypts your audio and decides who you can talk to. You and the person you're connecting with simply agree a password — say it out loud, or text it to each other — and each set it on the profile you use to talk to one another. The profile names don't have to match; only the passwords do.

### Setting and changing passwords

Where| What it does
---|---
**When you create a profile**|  Saving a new profile (File → Save as) asks you for a password right then.
**File → Change this profile's password** (Alt+F, P)| Changes the password on the profile you're using now. The box shows the current password in plain, readable text — so a screen reader reads the actual characters, not a row of dots — and you type a new one over it.
**Options → Profile passwords**| A list of every profile with its password in an editable box: a one-stop password manager. Edit any of them and press OK to save them all.

If you try to start sending or receiving on a profile that has no password yet, RemSound asks you to set one first (and offers to remember it on the profile so you don't type it again next time). Audio can't flow without a password — encryption is always on, there's no “off” switch.

**Passwords must be reasonably strong (new in 5.6).** The password is the only thing protecting your audio from someone who records your network traffic, so RemSound now refuses to stream on one that's easy to guess: at least 8 characters, and not a famously common password. If your existing password doesn't meet the rule, RemSound tells you the moment you try to stream and walks you through picking a better one — three unrelated words with a number, like `kettle9tiger42moon`, is easy to type and remember and very hard to guess. Set the _same_ new password on every machine you connect with. One honest note: the password is stored in the profile file in a recoverable form (so profiles can sync between your own machines) — anyone who can read your profiles folder can read the passwords, so treat that folder accordingly.

### When passwords don't match

If you connect to someone whose password is different from yours, RemSound shows a clear message — _“ You and [name] have different passwords, so no audio will pass between you”_ — so you know exactly what to fix. If the other person is on an older version of RemSound that can't encrypt, you'll be told they need to update.

### Two things worth knowing

  * **Everyone needs v3.3 or newer.** Because the audio is now scrambled, a v3.3 copy can only talk to other v3.3 (and later) copies. Anyone you connect with needs to update too.
  * **The password lives with the profile.** It's stored (lightly scrambled) inside the profile file, so it travels with the profile if you copy it to another machine or sync it through something like Dropbox. That's handy, but it means you should keep the profile file private — protect it the way you'd protect the password itself.



## 14. Latency and audio quality

Latency is the small delay between sound leaving one computer and arriving at the other. Four controls together shape the trade-off between latency and sound quality, all on the Audio profile tab:

  * **Audio latency in milliseconds (Alt+L)** — the main target for how much sound the receiving side keeps in reserve.
  * **Buffer smoothness (Alt+B)** — how hard the receiving side works to protect against sudden jitter.
  * **Packet size (Alt+P)** — Standard or Small. Small packets shave a couple of milliseconds off the sending delay, but double how many packets are sent.
  * **Continuous auto-tune** — lets the receiving side choose the latency target for you, re-checking every few seconds.



Plus the codec choice (PCM, Opus broadcast quality, or Opus live latency), also on the Audio profile tab. Most people only need to pick a codec and a smoothness level and leave the rest at the default.

### The sound-card cushion is automatic

Separately from the controls above — which manage the cushion against _network_ jitter — RemSound also keeps a small cushion at the sound card itself, to smooth over the tiny timing differences between your two computers' sound clocks. From this version, RemSound sizes that cushion to each card automatically: a card that moves sound in bigger chunks (some onboard and USB cards do) gets a little more room, while a fast professional interface stays tight. You don't set this or think about it — it settles on the right amount for whatever card you're using.

### Audio latency control

The **Audio latency** control tells the receiving side how much sound to keep in reserve as a cushion against uneven network timing. A bigger cushion means more delay but fewer clicks. A smaller cushion means less delay but more clicks when the network wobbles.

Setting| Best for| Trade-off
---|---|---
5–10 ms| Local network, same computer.| Crackles on any internet connection with even modest jitter.
20–40 ms| Stable Tailscale or wired internet.| A good balance — the added delay is usually inaudible.
50–80 ms| Internet with some Wi-Fi or jitter.| Noticeable delay, but very robust against drop-outs.
100 ms+| Bad networks; voice only.| The delay is definitely noticeable.

Smaller is better when the network can handle it. If you'd rather not think about this number, turn on continuous auto-tune (below) and leave it.

### Buffer smoothness

The **Buffer smoothness** list is a 1-to-10 scale for how patient the receiving side is when network jitter spikes. The default is 3.

Smoothness| Behaviour| Pick when
---|---|---
10 — smoothest| The receiving side tolerates the biggest jitter spikes without dropping any sound. Longest steady delay.| Bad Wi-Fi, a busy internet connection, music sessions where any click is unacceptable.
4–7| A middle ground. Smooths out most everyday internet jitter without much added delay.| Most internet sessions over Tailscale or a direct connection.
3 — default| Moderate protection; brief clicks possible when jitter spikes.| A stable internet connection or a quiet local network.
1 — tightest delay| The receiving side gives up immediately when sound is late. Frequent clicks, lowest delay.| Testing on a local network, experiments where you want the lowest possible delay.

Smoothness and the Audio latency control work together — smoothness controls _how the receiving side reacts_ when sound runs late; the latency value controls _how big a head-start it builds up_. A practical tip: if you can hear clicks, try raising smoothness by one or two before you reach for a bigger latency value.

### Packet size — Standard or Small

Two choices: **Standard** (the default) and **Small**. This controls how much sound each network packet carries:

Packet size| What changes| Pick when
---|---|---
Standard| One audio packet every 5 ms with PCM, every 20 ms with Opus broadcast quality, or every 2.5 ms with Opus live latency.| Any internet or Tailscale connection — any time you don't have a guaranteed-clean local network.
Small (local network only)| Halves how much sound each packet carries. Saves up to 2.5 ms of delay on the sending side.| A same-house local network over wired Ethernet, where the network simply isn't going to drop packets or jitter.

The saving is small — at most a few milliseconds end to end. Small packets are useful when you and your collaborator are on the same local network and want to chase every last millisecond. For any internet connection it's a false economy, because doubling how many packets are sent also doubles the chance of running into jitter at the wrong moment, which you hear as clicks.

### Locking to the audio clock (automatic)

RemSound always ties its sending timing to the sound device's own hardware clock, instead of letting Windows decide the pace. This used to be a **Lock to audio clock** checkbox that was off by default; it is now always on and there is nothing to set, because turning it off only ever added delay. On a WASAPI-only setup the sender takes its timing from the WASAPI capture; when an ASIO driver is also in use, both paths tighten independently.

> **Why it matters:** Windows' general timer can wake the audio loop with up to about 6 ms of wobble, even at top priority. At target latencies under about 15 ms, that wobble shows up as clicks. Locking to the audio clock takes Windows' timer out of the picture — the sound device itself drives the timing.

### Continuous auto-tune

The **Continuous auto-tune latency** checkbox hands the latency value over to RemSound itself. When it's on, RemSound watches how evenly packets are arriving, every few seconds, and nudges the latency target up if it's seeing late packets, or down if the network has been calm. It deliberately ignores a single one-off stall — the kind a driver or Windows hiccup causes once and never again — and only raises the cushion when late audio keeps arriving, so one brief blip doesn't balloon your latency for the rest of the session. The companion **Auto-tune latency interval (Alt+I)** combo box sets how often it re-checks — **3, 5, 10, 15, or 30 seconds**. Faster values react quickly to a change in the network but can feel a bit twitchy. Think of continuous auto-tune as a hands-off way to keep the cushion the right size as your network changes through the session.

If you turn auto-tune off, the latency value just stays wherever it last was.

### Artefact sound type

When the playback reserve briefly runs empty, RemSound has to fill the gap with something. The **Artefact sound type** list decides what that gap sounds like:

  * **Noise burst (default)** — a brief soft hiss that blends into music. Easy on the ear; it tells you something happened without being jarring.
  * **Click** — the gap is left unfilled, so you hear an obvious click each time. Use this when you want to _hear_ every problem (for example, while you're tuning the latency down).



### Opus repairs lost sound automatically (built in, no setting)

Both Opus modes can automatically repair lost audio: each packet quietly carries a small backup copy of the previous packet's sound, so the receiving side can rebuild any single packet that goes missing on the way. The result is that a single missing packet becomes inaudible — no click, no glitch — instead of the small pop you'd otherwise hear. Two missing packets in a row still produce one click; that's just a limit of how Opus works, not something you can change.

This happens on its own — there's no switch for it. PCM mode doesn't have it.

### Codec choice

Remember, the codec is the method RemSound uses to package the sound before sending it. There are three choices:

Codec| Quality| Network use| Delay added by the codec
---|---|---|---
PCM 48k 24-bit — uncompressed| Best, no loss at all| About 2.3 Mbps| None — the sound goes out exactly as it was captured.
Opus, broadcast quality — loss tolerant| Very good| About 200 kbps| About 12 ms.
Opus, live latency — for jamming and monitoring| Very good| About 320 kbps| About 5 ms.

The difference between the two Opus choices is what they trade for what. **Broadcast quality** packs sound into larger chunks — bigger packets, sent less often, more tolerant of a wobbly connection. **Live latency** packs sound into very small chunks and sends them eight times more often, getting your audio there with almost no codec delay at all — close to PCM — at the cost of being a bit more sensitive to a noisy connection. Broadcast quality is the right pick for anything across the open internet; live latency is for playing along together over a clean local network or a wired connection.

PCM gives the very best sound with no quality loss at all, but it uses about ten times the network bandwidth of Opus. Over the open internet, Opus is almost always the right choice.

Both Opus choices can automatically repair a single missing packet (see the section just above), so single drops are inaudible on both. PCM doesn't have that ability.

## 15. Keyboard shortcuts (within the main window)

Each tab has its own Alt+letter shortcuts. The same letter can do different things on different tabs without clashing — the shortcuts only work on the tab that's showing. Move between tabs with Ctrl+Tab and Ctrl+Shift+Tab, or jump straight to one with Ctrl and its number (Ctrl+1 for the first tab, and so on — the numbers follow whatever order you've set the tabs in).

### Connectivity tab

Key| Action
---|---
Alt+C| Focus the Connected peers list
Alt+E| Focus the Peer details box (for the highlighted connected peer)
Alt+M or F2| Rename the highlighted connected peer (F2 matches the Windows Explorer rename key)
Alt+D| Focus the Discovered peers list
Alt+R| Focus the Remembered peers list
Alt+A| Add peer by IP
Alt+L| Toggle Lock to these exact peer addresses
Alt+S| Focus the Connection status read-out

(The logging controls — Enable logs, Write logs now and the log-folder housekeeping — are on the Logging tab of the Preferences dialog; reach it via Options → Preferences or Ctrl+P, then use Alt+L / Alt+W within the dialog.)

### Audio inputs and outputs tab

Key| Action
---|---
Alt+D| Focus the ASIO driver list (hidden if no ASIO drivers are installed)
Alt+R| Toggle Receive audio
Alt+1| Focus ASIO outputs for received audio
Alt+2| Focus ASIO audio inputs to send
Alt+3| Focus WASAPI outputs for received audio
Alt+4| Focus WASAPI audio outputs to send
Alt+5| Focus WASAPI audio inputs to send
Alt+6| Focus the “How to send WASAPI audio” chooser (whole devices vs specific applications)
Alt+8| (Applications mode) Focus the Currently active applications list
Alt+9| (Applications mode) Focus the Remembered applications list
Alt+V| Focus the volume slider
Alt+S| Toggle Send my audio

### Audio profile tab

Some of these shortcuts shift depending on whether an ASIO driver is chosen. When one is chosen, the ASIO-path controls take the simpler Alt+L / Alt+T shortcuts, and the WASAPI-path controls move to Alt+W / Alt+Y so they don't collide.

Key| Action
---|---
Alt+U| Toggle Use CPU and Windows performance settings in high priority mode (for this profile)
Alt+C| Focus Audio codec
Alt+P| Focus Packet size
Alt+L| Focus the latency control — the ASIO path when an ASIO driver is chosen, otherwise the single Audio latency control
Alt+T| Toggle continuous auto-tune — the ASIO path when an ASIO driver is chosen, otherwise the single Continuous auto-tune toggle
Alt+W| (Only when an ASIO driver is chosen.) Focus the WASAPI-path latency control
Alt+Y| (Only when an ASIO driver is chosen.) Toggle the WASAPI-path continuous auto-tune
Alt+I| Focus the Auto-tune latency interval combo box. It drives the timing for the WASAPI auto-tune and, when an ASIO driver is chosen, the ASIO auto-tune too — one combo, both paths. Each path still settles at whatever latency its own calculation chooses; only the timing of the re-checks is shared. The label changes from “Auto-tune latency interval” to “Auto-tune interval — WASAPI and ASIO” once an ASIO driver is in use.
Alt+B| Focus Buffer smoothness
Alt+A| Focus Artefact sound type

### Volume, pan and EQ for peers tab

Present whenever the Volume, pan and EQ for peers tab is showing (it's shown by default; the toggle is “Show the volume, pan and EQ for peers tab” on the Appearance tab of Preferences). See Volume, pan and EQ for peers tab.

Key| Action
---|---
Alt+E| Toggle Enable volume, pan and EQ for all peers
Alt+U| Focus the Peers checklist
Alt+L| Focus the Volume slider
Alt+N| Focus the Pan slider
Alt+Q| Set peer EQ to default
Alt+M| Focus the EQ mode picker
Alt+A| Add band (parametric EQ mode only)
Alt+B| Focus the Bands list (parametric EQ mode only)
Alt+D| Delete band (parametric EQ mode only)

### File menu shortcuts (work from any tab)

Key| Action
---|---
Ctrl+S| Save the current profile (or Save as if on a new profile)
Ctrl+K| Open the Keyboard shortcuts dialog
Ctrl+P| Open the Preferences dialog
Ctrl+R| Start or stop recording (toggles)
Alt+K, R| Start or stop recording (via the menu — the Record menu is Alt+K, the item is R for “recording”)
Alt+K, O| Open the current recordings folder
Alt+K, C| Change the recordings folder
Alt+F, O| Open profile
Alt+F, R| Recent profiles (submenu — then 1..5 for the matching slot)
Alt+F, A| Save profile as
Alt+F, M| Rename the current profile
Alt+F, N| Minimise to tray
Alt+O, S| Recording settings
Alt+O, K| Keyboard shortcuts
Alt+O, W| Profile passwords
Alt+O, N| Manage named peers
Alt+O, P| Preferences
Alt+F, X| Exit

### Always available

Key| Action
---|---
**F1**| **Open this manual** in your default web browser. Works anywhere in RemSound — the main window, every dialog, and the profile picker on first launch.
Ctrl+Tab / Ctrl+Shift+Tab| Move to the next / previous tab
Ctrl+1…Ctrl+9| Jump straight to a tab by its position — Ctrl+1 is the first tab, Ctrl+2 the second, and so on. The number follows the current order, so if you reorder the tabs (Preferences → Appearance) the numbers move with them. Works in the main window and in the Preferences dialog.
Tab / Shift+Tab| Move between controls within the current tab
Spacebar| Tick or untick an item in any device list, or toggle the focused checkbox
Up / Down| Move between items in any list
Alt+F4| Close (the standard Windows shortcut)

## 16. Global hotkeys (work even when minimised)

Your keyboard shortcuts are **shared across all your profiles** — set one once and it works on every profile, and stays put when you switch between them. (Before version 4.4 they were saved separately inside each profile, so a shortcut set on one profile wouldn't work on another. If you're upgrading from before version 4.4, the first time you run the new version RemSound offers to **bring your shortcuts across** : it shows a list of your profiles, and you pick the one you set your shortcuts up in — or choose to start fresh with the defaults.)

You set these up in the Keyboard shortcuts dialog (Ctrl+K, or Options → Keyboard shortcuts). The dialog is a single list of every hotkey you can set: **Enter** sets the highlighted row, and **Del** — or the **Clear this shortcut** button — clears it (back to _not set_). When you're setting a shortcut, you can press **Delete** inside that box to leave it unassigned. **Escape** or the Close button closes the dialog. The defaults:

Hotkey| Action| Default
---|---|---
Receive mute| Mute / unmute incoming sound (this computer)| Ctrl+Shift+Alt+R
Send mute| Mute / unmute outgoing sound (this computer)| Ctrl+Shift+Alt+S
Tray toggle| Show / hide the main window| Ctrl+Shift+F10
Quick profile switch| Pop up a list of all your profiles and switch to one — works from anywhere, even with RemSound in the tray (where it stays after the switch). See _Quick profile switch_ below.| Unset
Volume up / down| Adjust this computer's received-sound volume| Unset
Start / Stop recording| Start or stop a recording on this computer. The same toggle as the Record menu's start/stop item and the in-app Ctrl+R, but it works system-wide (RemSound doesn't need to be the active window). See Recording to a file for what gets captured.| Unset
Send remote RemSound volume up to peers| Tell every connected peer to raise their RemSound volume slider by 5 points (only obeyed by peers that have ticked “Accept remote volume commands”). It doesn't change your own volume. See Remote control.| Unset
Send remote RemSound volume down to peers| The same, but lowering.| Unset
Send remote RemSound receive mute toggle to peers| Tell every connected peer to toggle their RemSound receive mute.| Unset
Send Windows global volume up to peers| Tell every connected peer to nudge their _Windows_ volume up by one step (about 2%, the same as their keyboard volume key). This affects every app on the receiving computer, not just RemSound. Hold the hotkey down for bigger jumps. See Remote control.| Unset
Send Windows global volume down to peers| The same, but lowering.| Unset
Send Windows global mute toggle to peers| Tell every connected peer to toggle their Windows mute.| Unset
Speak the RemSound status information| Read the whole status line out loud through your screen reader — the connection time, how many peers you have, whether sound is flowing, and how healthy the link is — from anywhere, even with RemSound in the tray. Just for screen-reader users; see Hearing the status on demand below.| Unset
Toggle volume, pan and EQ for all peers| Flip the one master switch on the Volume, pan and EQ for peers tab from anywhere, so you can drop all your per-person shaping in and out without leaving the app you're in. See that tab for what the switch does.| Unset

You can change any of these to whatever combination you prefer. Each accepts modifiers (Ctrl, Shift, Alt) plus one ordinary key.

### Quick profile switch

Once you've given **Quick profile switch** a key, pressing it anywhere pops up a small list of every profile you have. Arrow to the one you want and press Enter (or click it) to switch straight to it. The profile you're currently on is marked in the list. A sound plays as the list opens, and the profile-switch sound plays the moment you pick one. Press Escape to close the list without switching.

If RemSound was minimised to the system tray when you pressed the hotkey, it switches the profile and **stays in the tray** — the window doesn't jump up in front of whatever you're doing. So you can change profiles mid-task without losing your place.

### Hearing the RemSound status on demand

The main window has a **status line** that updates every second with how long you've been connected, how many peers you have, whether sound is flowing, and how healthy the connection is. Normally your screen reader reads it like any other text — but now and then, for reasons that have nothing to do with RemSound, a screen reader loses sight of it and says there's nothing there.

This hotkey is the cure. Give **Speak the RemSound status information** a key in the Keyboard shortcuts dialog, and from then on pressing it reads the whole status out loud, wherever you are — even when RemSound is tucked away in the tray or another program is in front. It's unset to start with, so the key is yours to choose.

It reads the status out **a line at a time** — peers, ping, uptime, data rates, totals — so each piece lands as its own short phrase rather than one long run-on, and big totals show in gigabytes once they pass a gigabyte. A quick **double press** of the same hotkey **copies the status to the clipboard** instead of reading it, so you can paste it to someone if you're comparing notes.

This one is just for screen-reader users: it talks straight through your screen reader. It works with the screen readers RemSound's speech helper supports — **NVDA** , JAWS, Window-Eyes, System Access, SuperNova and ZoomText — and falls back to Windows' own built-in speech if none of those is running. If you don't use a screen reader, just leave this one unset.

### Your screen reader reads out the hotkeys

Once a hotkey is set, your screen reader reads it out whenever you land on the menu item or control it's tied to — for example, moving onto **File → Open profile** announces “Ctrl+O”, and a control with a global hotkey announces “press [your key] anywhere”. So you can learn and confirm your shortcuts just by arrowing around the window, without coming back to this dialog.

## 17. Remote control: adjusting a peer's listening volume from your end

Here's the situation this is for: you're on your laptop, listening to sound coming from your desktop, and you've got NVDA Remote open so you can drive the desktop using your laptop's keyboard. Every key you press goes to the desktop — including any volume key on the laptop, which now never reaches the laptop itself. There's no way from inside that NVDA Remote session to nudge the laptop's listening volume without breaking out of the session.

RemSound's **remote control** feature gives you a way around this: you set up a hotkey on the desktop (the computer your keyboard is talking to) that sends a command across the audio link, telling the laptop's RemSound to raise, lower or mute its own listening volume. You stay in NVDA Remote, and the laptop responds.

### Two kinds of remote command

There are two independent sets of remote-control hotkeys, both governed by the same opt-in toggle on the receiving end. Pick whichever fits the situation, or set up both:

Set| What the receiving computer does| Best for
---|---|---
**RemSound app volume**|  Adjusts the receiving peer's RemSound volume slider by 5 points per press, or toggles RemSound's receive mute. Only RemSound's sound is affected.| Fine adjustments while RemSound's slider still has room to move. Doesn't touch the screen reader's volume or any other app.
**Windows global volume**|  Nudges the receiving peer's Windows master volume up or down by one step (about 2%, exactly the same as pressing the keyboard volume key there), or toggles the master mute. This affects every app on the receiving computer, including the screen reader.| Real-world “I need this louder” situations, especially with hearing impairment, or when RemSound's slider is already at the top. Hold the hotkey down to ramp up over a longer range.

Both sets target the receiving computer. Neither one changes anything on the sending computer.

### How to set it up

  1. On the computer that should _respond_ to remote commands (the one you're listening on — the laptop in the example): open **Preferences** (Ctrl+P) and tick **Accept remote volume commands from peers**. Save the profile (Ctrl+S) so the choice sticks. (One toggle covers both kinds of remote command.)
  2. On the computer that should _send_ remote commands (the one your keyboard is driving — the desktop in the example): open the **Keyboard shortcuts** dialog (Ctrl+K). Set whichever of the six remote-control rows you want:
     * **Send remote RemSound volume up / down to peers** — nudges the receiver's RemSound slider.
     * **Send remote RemSound receive mute toggle to peers** — toggles the receiver's RemSound mute.
     * **Send Windows global volume up / down to peers** — nudges the receiver's Windows volume.
     * **Send Windows global mute toggle to peers** — toggles the receiver's Windows mute.
Use whatever key combinations you prefer (for example Ctrl+Shift+Up / Ctrl+Shift+Down for one set, and Ctrl+Alt+Up / Ctrl+Alt+Down for the other). These are global hotkeys: they work as long as RemSound is running, no matter which app is in front.
  3. That's it. Press the hotkey on the desktop — the laptop responds just as it would if you'd pressed the matching key on the laptop directly, and you hear the change without leaving the NVDA Remote session. Hold the Windows-volume hotkey down for a steady ramp, since Windows' key repeat fires the step over and over.



> **A heads-up about “Windows global volume”:** the Windows volume affects _everything_ on the receiving computer — not just RemSound. NVDA's voice gets louder with it, browser sound gets louder, every notification gets louder. For a hearing-impaired listener that's usually exactly what you want (everything gets to a usable level), but it's a very different thing from the in-app slider, which only changes RemSound's sound. Pick the right one for the situation.

### It works both ways

The feature is symmetric: both computers can both send and accept. If you set up hotkeys on both ends and tick “Accept remote volume commands” on both ends, either side can adjust the other's volume. There's no fixed “controller” and “controlled” computer.

**Security:** remote commands are locked with the profile password, the same way the audio is. A command only works when both machines share the password, so nobody on the network can fake one and mute your machine — important when the machine's sound is also your screen reader. Both ends need version 5.6 or newer for remote volume to work; a command from an older version is ignored (the log records it as rejected).

### What it does not touch

  * The RemSound app-volume commands change RemSound's _receive volume slider_ on the target computer, not the Windows volume. So your peer can't accidentally turn down a video call or your screen reader with those.
  * It only works between peers who are already connected (each one has the other ticked in their connected-peers list). The list of people you've ticked is the gatekeeper — an unticked peer can't change your volume.
  * The opt-in tick is per-profile, so a setup you've marked as your “trusted home pair” can have it on while a one-off jam-session profile keeps it off.
  * Remote commands travel on the same audio channel as the sound and the health check-ins (47830 by default), so there's no extra firewall rule to add.



> **Tip for troubleshooting:** the log file (Preferences dialog → Enable logs) records every remote-control command sent and received, including `IGNORED` entries when an incoming command was turned down — either because the sender wasn't in your list of ticked peers, or because “Accept remote volume commands” was off. Handy for working out “why isn't my hotkey doing anything” without guessing.

## 18. Startup behaviour

Startup behaviour now lives on the **Startup behaviour** tab of the Preferences dialog (**File → Preferences**, or Ctrl+P). It has three independent toggles, plus a profile picker that appears when the third one is on. Each tick is saved straight away — there's no OK or Apply button. (It used to be a separate item on the Options menu; it moved into Preferences in the 2026 cue overhaul.)

Toggle| What it does
---|---
**Start minimised to tray (Alt+M)**|  RemSound hides itself in the system tray as soon as the main window finishes loading. The window is still reachable from the tray icon and the tray hotkey. Useful together with the auto-start option below, for a fully hands-off “turn the computer on, start streaming” setup.
**Start RemSound automatically when this user logs in (Alt+A)**|  Adds RemSound to (or removes it from) Windows' standard list of programs that start when you log in. After ticking it, Windows launches RemSound the next time you log in. It also appears under Task Manager → Startup, where you can disable it too. It applies to your account only — it doesn't need admin rights and doesn't affect anyone else who uses the same computer.
**Start with a specific profile (Alt+P)**|  When ticked, RemSound skips the startup profile picker and loads the profile you choose in the list below. When unticked, the profile picker shows as normal. If you don't have any saved profiles yet, ticking this shows a one-time warning and stays unticked — save a profile first, then come back. To bring the picker back temporarily without losing your choice, untick the box, start RemSound normally, then tick it again afterwards.

**Profile to start with (Alt+L)** — the list of your saved profiles. It only shows when the third toggle is on. Pick a profile and the choice is saved straight away.

### Combining the three for a hands-off start

  1. Save a profile with the device choices, peers, and sound settings you want for “always-on” use.
  2. Go to the Startup behaviour tab in Preferences. Tick all three: _Start minimised_ , _Start automatically when this user logs in_ , and _Start with a specific profile_ — then pick the profile you just saved.
  3. Close the dialog. Reboot, or log out and back in, to test — RemSound starts itself, loads the profile, and goes straight to the tray. Sound starts flowing as soon as the peer is reachable.



> **Where these are stored:** the start-minimised choice and the start-with-profile name are kept in a small settings file on this computer. The auto-start toggle is kept in Windows' standard startup list — you turn it on or off from this dialog, or from Task Manager → Startup.

## 19. Audio cue sounds

RemSound plays a short sound at moments where you might want an audible confirmation that something just happened. These are called **cue sounds**. A range of events have a cue:

Cue| Plays when
---|---
**Connect sound**|  A peer goes from “trying” or “unreachable” to actually connected.
**Disconnect sound**|  A previously-connected peer drops off (network blip, peer closed RemSound, computer went to sleep, etc).
**Recording start sound**|  You start a recording.
**Recording stop sound**|  You stop a recording.
**Profile saved sound**|  A profile is saved — whether via File → Save or File → Save as.
**Profile switched sound**|  You switch to a different profile — from the Recent profiles menu, the Quick profile switch popup, or File → Open profile. It plays the moment you pick the new profile. It deliberately does _not_ play on a fresh start into your first profile, so it isn't layered on top of the connect sound at launch.
**Profile menu open sound**|  The Quick profile switch popup opens.
**Update sound**|  An update is about to install — it plays just before RemSound closes to update itself. Handy when updates install silently in the background, so you're not caught off guard when RemSound restarts. Plays whether you ran the update by hand or it installed on its own.
**Startup sound**|  RemSound has finished starting up. It plays once at launch, even when RemSound opens straight to the notification area, so you know it's running.
**Send turned on / off sound**|  You turn sending your audio on or off — whether by ticking the **Send my audio** box in the window or by pressing its mute shortcut. There's a separate sound for on and for off.
**Receive turned on / off sound**|  You turn receiving audio on or off — from the **Receive audio** box or its mute shortcut. Again, a separate sound for on and for off.
**Minimise (hide) sound**|  RemSound's window minimises to the notification area (hides).
**Restore (show) sound**|  RemSound's window is brought back from the notification area (shows).
**Checkbox ticked / unticked sound**|  You tick or untick _any_ checkbox anywhere in RemSound — a click for ticked, a different one for unticked. This gives instant feedback on which way a box just went, which is especially handy in the busy inputs and outputs lists. There's a separate sound for ticking and for unticking.
**Switch tabs sound**|  You move between tabs anywhere in RemSound — the row of tabs in the main window, or the tabs in a dialog like Preferences. It plays each time you land on a different tab (with Ctrl+Tab, or the arrow keys when the row of tab names has focus).

All of these cues play through your default Windows sound output, which is separate from the audio RemSound is sending or receiving. They don't appear in a normal recording. (The exception: if your sending side is capturing the very output device the cues play through, then they get captured along with everything else from that device.)

### The Audio cues tab

Open **File → Preferences** (or Ctrl+P) and go to the **Audio cues** tab. (Preferences is organised into six tabs — General, Appearance, Audio cues, Startup behaviour, Update settings and Logging — which you move between with Ctrl+Tab, Ctrl and a number, or the arrow keys when the row of tab names has focus.)

The **Audio cue sounds (Alt+N)** list shows every cue by name. Use the up and down arrow keys to move between them — as you land on each cue, RemSound plays its current sound, so you can hear what's set just by arrowing through.

### Turning a cue on or off, and choosing its sound

Just below the cue list is a **Choose sound (Alt+D)** list, which controls whichever cue is highlighted above. Its first entry is **(none)** ; the rest are the built-in sounds that cue ships with. Arrow through it and RemSound plays each one as you land on it:

  * Land on **(none)** and the cue is switched **off** — nothing plays for that event.
  * Land on a sound and the cue is switched **on** and set to use that sound.



Every cue starts switched on, using its first sound. **"(none)" is how you silence a cue** — there are no separate tickboxes any more. Your choices are remembered for next time (per-event cues travel with the active profile; the app-level cues like startup and minimise are remembered for the whole installation).

The tick settings are **saved with the active profile** , so different profiles can have different combinations of cues on. For example, a “quiet listening” profile might have all cues off, while a “live monitoring” profile keeps them on. When you save the profile (Ctrl+S), the new settings travel with it.

### Previewing and choosing a different sound

Below the list are two buttons that act on whichever cue is currently highlighted in the list:

  * **Play [cue name] (Alt+P)** — previews the cue's currently-configured sound through your default Windows output, so you can hear it without having to trigger the event. Works regardless of whether the cue is ticked (so you can listen before deciding to enable it).
  * **Browse for [cue name] … (Alt+B)** — opens a Windows file picker so you can choose your own WAV file for this cue, replacing the default. RemSound only accepts `.wav` files. Once picked, the button's label changes to “(custom)” to remind you the cue is using your file rather than the default. The next time the event fires, your custom sound plays.



Both buttons' labels update as you arrow through the list, so you always know which cue you're about to act on.

### If a cue's sound file goes missing

If a cue is switched on but RemSound can't find its sound file — for example you delete a WAV you'd browsed for — RemSound brings its window to the front (even when it's minimised) and tells you which cue and which event it was, then switches that cue to **(none)** so it stops trying. Pick a sound for it again in the **Choose sound** list, or Browse for a new file, to turn it back on.

### Keyboard clicks while typing

Just below the cue controls is a tickbox, **Play keyboard clicks when typing into any edit field (Alt+K)** , which is on by default. With it ticked, typing into any text box anywhere in RemSound plays a soft click on each key, so you get an audible sense of your typing. It only sounds while your cursor is actually in an edit field — move out of the field and it stops.

**Password boxes are treated specially:** they play the same key click _and_ a second, distinct sound at the same instant, so you can tell by ear when you're typing into a masked password field rather than an ordinary one. That second sound only happens in password fields.

Untick the box to switch all of this off. The setting applies to the whole installation.

Custom sound choices are **saved with the active profile** , the same way the tick states are. Different profiles can have completely different cue palettes — a “studio” profile might use one set of sounds, a “broadcast” profile another. The custom files themselves stay where you picked them on your disk; the profile just remembers their paths.

### Going back to the default sound

To revert a cue to its default sound, **right-click** the _Browse for [cue name] …_ button and pick **Use default sound**. The custom path is forgotten and the cue goes back to playing the default WAV that ships with RemSound. The right-click option is greyed out when the cue is already using its default. (Alternatively, click _Browse_ and pick a file from RemSound's own `default sounds` folder — alongside the program — and RemSound treats that as “use default” and clears the override automatically.)

### Where the cue sounds live

RemSound keeps the built-in cue WAV files in a `default sounds` folder alongside the program. Each cue ships with a small set of numbered sound files there. They're named after the cue with a number on the end — for example `connect 1.wav` and `connect 2.wav` for the connect cue, `record start 1.wav` and `record start 2.wav` for the recording-start cue, and so on. The **Choose sound** list described above simply picks between the numbered files a cue has.

Because these are RemSound's own built-in sounds, an update can refresh them — if a future version ships an improved default, you'll get it. To use a sound of your _own_ for a cue, don't drop a file into `default sounds` (an update would overwrite it); instead use the **Browse** button, which links the cue to your file wherever you keep it. That link is remembered and never touched by an update, so your chosen sound always stays put.

If a cue's WAV file is missing — either the default file doesn't exist or a custom path points at a file you've since deleted — the cue stays silent rather than producing an error. RemSound logs a note in the diagnostic log (if logging is on) so you can see what happened.

> **Tip for sound designers:** the defaults are deliberately short and simple so they stay out of the way. If you'd like the cues to feel more in-character with a particular profile, the custom-sound feature is designed for exactly that. Keep WAV files short (well under a second usually works best) so cues don't overlap with each other on a busy day.

## 20. Updating RemSound

RemSound can check for a newer version on a schedule you choose, prompt you to install it, and either ask first or do it quietly. There's also a one-press “check now” button so you don't have to wait for the timer.

### Settings in Preferences

Open **Options → Preferences** (or Ctrl+P) and go to the **Update settings** tab:

Setting| Shortcut| What it does
---|---|---
**Check for updates on startup** (checkbox)| Alt+S| When ticked, RemSound has a quiet look for a newer version a few seconds after each launch. On by default. Combined with _Silently install updates_ below, this means leaving RemSound to keep itself up to date without you ever needing to press anything. Untick if you'd rather only ever check on a timer or by pressing the manual button.
**Then check every** (drop-down)| Alt+U| How often RemSound checks for a newer version in the background _after_ launch. Choices: _Never_ , _Every hour_ , _Every 6 hours_ , _Every 24 hours_. The default is _Every 24 hours_. Your choice is remembered between launches; if you set it to _Never_ and you've also unticked the startup check, the only way an update arrives is through the manual button below.
**Check for updates now** (button)| Alt+N| Checks for a newer version straight away. If you're already up to date you get a small popup saying so. If there's a newer version, you get a confirmation dialog with the release notes and a Yes / No to install. The same button is in the Help menu (Alt+H, C).
**Silently install updates when available** (checkbox)| Alt+I| When ticked, the background and startup checks install any available update without asking — RemSound downloads it, closes briefly, swaps the files, and reopens itself. Off by default. The startup check shows a brief notice first so you can see what's about to happen (see below). The _manual_ “Check for updates now” button always asks first, no matter how this checkbox is set.
**Only install updates within this time range** (checkbox)| Alt+T| When ticked, _automatic_ installs (the startup check and the background timer) only go ahead inside the daily time range you set below — so an update never closes RemSound and kills your sound while you're mid-session. If an update is found outside the range, RemSound notes it and quietly retries the moment the range opens. Off by default. The manual “Check for updates now” button ignores the range — if you ask by hand, you get it straight away.
**Start time** / **End time** (drop-downs)| Alt+A / Alt+D| The daily range for the checkbox above, in 15-minute steps from 00:00 to 23:45. They only light up when the range is enabled. The default is 01:00 to 06:00 — overnight. A range whose end is at or before its start runs past midnight: 22:00 to 06:00 covers late evening straight through to early morning. The start minute counts as inside the range; the end minute is outside it.
**Show what's new after each update** (checkbox)| Alt+H| When ticked, the first time RemSound opens after an update has installed, it pops up the About box — which starts with the notes for the version you just got — so you can see what changed. Off by default. It only happens once per update, never on an ordinary restart, and never on a fresh install.

### The brief notice before a silent update installs

If RemSound finds an update right after launch and is set to install silently, it now shows a small window so you're not surprised when the app closes a few seconds in. The window says “RemSound vX.X is ready to install” with three buttons:

  * **Install now** — installs straight away. This is the default; press Enter or wait through the countdown to pick it.
  * **Skip this version** — leaves the update alone for this launch. (RemSound may offer it again next time it checks.)
  * **Postpone** — close the notice without installing now. The next scheduled background check will pick it up again.



A short countdown picks _Install now_ automatically if you don't choose anything — long enough to read the version number, short enough that walking away from your desk doesn't block the silent update. Esc has the same effect as Postpone.

### What happens during an install

RemSound can't replace its own program file while it's running, so it hands the job to a fresh copy of the new version, which does the swap once RemSound has closed:

  1. RemSound downloads the new version into a temporary folder kept on your own machine, away from the install folder.
  2. It starts the new copy from that temporary folder, and closes itself.
  3. The new copy waits for RemSound to close fully, then moves your old program files aside and copies the new ones into place. If a file is briefly in use, it waits and retries rather than giving up.
  4. It reopens RemSound on the same profile you were running, and clears the temporary folder away.



You'll see the window close, then reopen on the new version within a second or two. Anything that was unsaved in the old session (a profile you were partway through editing, for example) is lost — RemSound will not save it for you before installing. Save first if you've been making changes.

### The same profile picks up automatically after an update

When the install finishes and RemSound reopens, it loads the same profile that was running just before the update — you don't see the profile picker, and your devices, peer list, codec and latency settings all come back exactly as they were. This means a silent update in the middle of a session drops the audio briefly while the install finishes, then your session reconnects on its own. You don't have to be at the computer when it happens.

This is a one-shot, just-after-the-update behaviour. The very next time you launch RemSound manually (from the desktop, the Start menu, or the tray icon), it follows your normal startup choice — the picker if that's how you've set it, or your chosen startup profile if you've picked one in Preferences → Startup behaviour.

If the profile that was running can't be found after the update (you'd renamed or moved it during the session, for example), RemSound falls back to your normal startup behaviour rather than getting stuck.

### If an install fails

The update download is best-effort: a flaky network, or a temporarily-unavailable version, will pop up a message saying it couldn't finish, and leave your running version untouched. The address of the download page is in that message, so you can get the new version in a browser and install it by hand if you need to. If you installed RemSound into `Program Files` without giving your account permission to write to that folder, the install can't replace the files there — either fix the permission or move RemSound to a folder you can write to (somewhere inside your own user folder, for instance).

**If the file swap itself can't finish** — for example because a sync app or another program was holding one of the files open and wouldn't let go — RemSound _puts your previous version back exactly as it was_ and reopens it, rather than leaving you with a half-finished install. It writes a short note called `update-failed.txt` next to the program explaining what happened. Nothing is broken — just try **Help → Check for updates** again, and it almost always goes through on the next attempt. If a sync app like Dropbox is involved, closing RemSound and giving it half a minute to settle before retrying helps.

A step-by-step record of every update is kept in a file called `updater.log` in the install folder — useful if a failure keeps happening and you want to share it for diagnosis.

### The About dialog and release notes

To see which version you're on without checking for updates, open **Help → About RemSound** (Alt+H, A). The dialog shows the version number and the release notes for the version you're running, in a scrollable read-only box. Close (or Esc) dismisses it.

## 21. Recording to a file

RemSound can save the sound passing through it to a file on your computer — useful for keeping a copy of a music session, capturing a long jam for editing later, or just saving a one-off voice exchange you want to come back to.

### What gets recorded

Recording captures the sound at fully-mixed, fully-finished points: for the received side, after volume and mute have been applied (so the file matches what you hear); for the sent side, the raw captured sound just before it's packaged for sending (so the file is the same whatever codec you chose). The three source choices:

  * **Record both sent and received audio** — a single file with both directions mixed gently together. This is the default, and the right choice for capturing a complete two-way exchange.
  * **Record all received audio** — the full mix of everything coming in from connected peers.
  * **Record all sent audio** — what your microphones, captured speakers and ASIO inputs are sending out. Useful for checking what your collaborators are actually hearing from you.



### File formats

All four formats record at a 48 kHz sample rate, and every row in the attributes list states the rate clearly so it's never in doubt.

Format| What you get| When to pick it
---|---|---
**WAV** (default)| An uncompressed file. No quality loss, but large — about 17 MB per minute at 24-bit stereo. Bit-depth choices: 16-bit, 24-bit (the default), or 32-bit float (the highest quality). Plus stereo or mono.| Keeping a master copy, editing in audio software, anything where you might want to re-master later.
**MP3**|  A compressed file at one of four bitrates: 128 / 192 / 256 / 320 kbps. Stereo or mono. MP3 plays just about everywhere.| Long sessions where file size matters; quickly sending someone a listen-once file.
**OGG-Opus**|  A compressed file using Opus, at one of four target bitrates: 96 / 128 / 192 / 256 kbps. Stereo or mono. The file extension is `.opus`.| Smaller files than MP3 at similar quality; plays in most modern players (VLC, mpv, web browsers).
**FLAC**|  A compressed file with no quality loss at all. Bit-depth choices: 16-bit or 24-bit (the default). Stereo or mono. Files are typically about half the size of the same recording as WAV, with no loss of quality.| Keeping a master copy when you also want a sensible file size — it plays back identically to WAV but is half the size.

**Surviving a crash.** All four formats are designed to leave a playable file behind even if RemSound crashes partway through a recording. You lose at most about 5 seconds of recently-captured sound on a crash, never the whole session.

### Start and stop sound cues

RemSound plays a short ding when a recording starts and another when it stops, so you have an audible confirmation that the toggle actually took effect. These are two of the cues described in Audio cue sounds. You can turn either or both off, replace them with your own WAV files, and preview them from Preferences. The built-in defaults are the `record start` and `record stop` sounds in the **default sounds** folder alongside the program.

### Where recordings go

Inside your recordings folder, RemSound keeps a folder for each **date** — for example `2026-07-04` — so your recordings sort neatly in order. Everything from a given day goes into that day's folder.

All the times below are written 24-hour, as hours-minutes-seconds (`HH-MM-SS`), so files and folders line up in time order in Explorer.

  * A normal (non-split) recording is a single file named like `14-30-05 RemSound recording DESKTOP-ABC.wav` — the time, then “RemSound recording”, then this computer's name (and the right extension for your chosen format).
  * A **split** recording (when you tick “Split recording into separate tracks”) is instead a _folder_ named like `14-30-05 RemSound recording multi track`. Inside it there's one file per peer, named `<peer name> 14-30-05.wav` (the friendly name you've given the peer if it has one, otherwise its computer name, or its address if it has neither), plus your own send as `<your computer name> 14-30-05.wav`.



You can change the folder via **Record → Change recordings folder**. Choosing a different folder saves that location into your current profile, so it travels with the rest of your settings — switching profiles can switch your recording destination too. If a saved profile points at a folder that doesn't exist on the computer loading it, the recorder quietly falls back to the default location for that computer.

**Record → Open current recordings folder** opens Windows File Explorer on whatever folder is currently set, creating it on the spot if no recording has been made there yet.

### Starting and stopping

There are four ways to start or stop a recording:

  * **Ctrl+R** from anywhere in the main window — a toggle. The menu item text switches between “Start recording” and “Stop recording” to show the current state.
  * **Record → Start recording** (or Stop, when one is in progress).
  * The **Start / Stop recording** global hotkey — works system-wide, even when RemSound is minimised or isn't the active window. It's unset by default; set a combination in the Keyboard shortcuts dialog (Ctrl+K). See Global hotkeys.
  * Closing RemSound while a recording is running finishes the file cleanly — you don't lose anything if you forget to stop it manually.



Recording happens in the background, so it doesn't affect the sound or the network. If your disk ever can't keep up, the recorder drops the oldest queued sound (never the newest) and notes it in the log; in practice you'll only see that on a fully-saturated USB stick or a very slow network drive.

### Recording settings dialog

Reached via **Record → Recording settings**. Two tickboxes sit at the top, then up to five keyboard-navigable lists laid out left to right. FLAC's compression level has its own list, but it only shows when the file format is FLAC.

Control| What it does
---|---
**Split recording into separate tracks** (tickbox)| Instead of one mixed file, a recording becomes a _folder_ with one file per connected peer — each holding only that peer's sound — plus one file for your own send. Which of those files you get follows the Recording source choice below: _Record all received_ gives you the peer files; _Record both sent and received_ gives the peer files plus your own; _Record all sent_ gives just your own.
**Bypass pan and EQ when recording** (tickbox)| Records the raw sound — before any volume, pan or EQ you've set on the Volume, pan and EQ for peers tab — even though you still hear the shaped version. Left off (the default), the recording captures what you actually hear, including your shaping; and on a split recording each peer's own file carries that peer's own shaping.
List| Shortcut| What goes in it
---|---|---
**Recording source**|  Alt+S| Record both sent and received (the default), Record all received, or Record all sent. See the source explanation above.
**File format**|  Alt+F| WAV / MP3 / Ogg-Opus / FLAC. The attributes list (and FLAC compression list) to the right change whenever you change this.
**Audio format attributes**|  Alt+A| Changes to match the format, with the 48 kHz sample rate stated on every row so there's no ambiguity. WAV: three rows (16-bit / 24-bit / 32-bit float). MP3: four rows (128 / 192 / 256 / 320 kbps). OGG-Opus: four rows (96 / 128 / 192 / 256 kbps). FLAC: two rows (16-bit / 24-bit).
**FLAC compression level**|  Alt+L| Shown **only when FLAC is the chosen file format**. Nine rows for levels 0 to 8, with friendly labels on the ends (“0 — fastest, biggest file”, “5 — default”, “8 — slowest, smallest file”). Every level produces an identical, no-loss recording — it's purely a trade-off between encoding speed and file size.
**Channels**|  Alt+C| Stereo or Mono. Applies to every format.

OK (Alt+O) saves your choices to the current profile. Cancel (Alt+N) or Esc discards them. Settings are saved with the profile as usual — changes here mark the profile as having unsaved changes, and you'll be asked about them on exit if you haven't saved.

## 22. Logs and diagnostics

Everything to do with logging lives on its own **Logging** tab in the Preferences dialog (Options → Preferences, or Ctrl+P). If logging is turned on (the **Enable logs** checkbox there, on by default), RemSound writes a log file each session into a `logs` folder inside **user settings and logs** — the same folder your settings and profiles live in. One file per launch.

The file contains two kinds of rows:

Kind| Contents
---|---
EVT| Event lines — startup, a peer being selected, capture starting, errors, and so on.
SNAP| One-second snapshots of running figures: codec, latency target, how much sound is buffered, packets sent, packets received, drop-outs, drops, and peer round-trip times.

The **Write logs now** button on the Logging tab (Alt+W within the dialog) writes a “user requested write logs now” marker into the log, so you can find that moment in the file afterwards.

### Keeping the logs folder tidy

Log files are small, but if you leave logging on for months they add up. The **Logging** tab has three ways to keep the folder under control, all switched off to begin with so nothing is ever deleted unless you ask for it:

  * **Warn at startup if the logs folder is larger than … megabytes** (Alt+S) — tick this and pick a size, and the next time RemSound starts it checks the folder and pops up a friendly notice if it has grown past that size. It only warns; it never deletes anything itself. The size box (Alt+M) starts at 100 megabytes and stays greyed out until you tick the box.
  * **Delete logs older than … days old** (Alt+D) — tick this and pick a number of days, from 1 to 30, and each time RemSound starts it quietly clears out any log older than that. The log it's writing right now is never touched. The days box (Alt+Y) starts at 14 and stays greyed out until you tick the box.
  * **Delete all logs** (Alt+A) — a button that clears out every log file in one go. It asks you to confirm first, Yes or No, then tells you how many it removed. Again, the log RemSound is writing right now is kept; everything else goes.



Logs are plain text and can be opened in any text editor, or in a spreadsheet. The most useful figures when something feels wrong:

  * **BufferMs** — how much sound is queued up ready to play. It should sit close to your latency target.
  * **Underruns** — how many times the playback reserve ran dry. Each one is a tiny click. A few per minute is normal over the internet; hundreds per second means something is broken.
  * **Drops** — packets thrown away because the reserve overflowed. This should stay near zero in normal use.
  * **Heartbeat** — the round-trip time to each connected peer. `pending` / `unreachable` / `stale` mean there's a problem.
  * **OpusFecRecoveries** — a running total of single missing packets that Opus quietly repaired. A number that's growing means Opus is saving you from clicks. Only meaningful when an Opus codec is in use.
  * **OpusUnrecoveredGaps** — a running total of multi-packet losses that Opus couldn't repair. Each one is an audible click. It stays at 0 on a clean connection; small numbers are normal over the internet.



## 23. Command-line options

RemSound is normally a windowed program you click to open. But it can also take **command-line options** — short text instructions you type after the program name. They are handy for three things: checking a machine quickly (what devices are present, does the audio path work at all), getting a support report to send to whoever helps you, and starting RemSound a particular way from a shortcut or a script.

To use them, open a command prompt (press the Windows key, type `cmd`, press Enter), then run RemSound with the option after it. If RemSound is on your desktop you can type the whole path in quotes, for example:


    "C:\Users\you\Desktop\RemSound\RemSound.exe" --devices


The options that just report something print their answer straight into the same command window as plain text — a screen reader reads it normally — and then RemSound exits without opening a window. The start-up options open RemSound as usual, just set up the way you asked.

### Options that print something and then exit

Option| What it does
---|---
`--help` or `-h`| Lists every option, the same as this section in short form.
`--version`| Prints which version of RemSound is installed, for example “RemSound 3.9”.
`--devices`| Lists every microphone and line-in, every speaker and headphone output, and every ASIO driver on the machine — each with its sample rate, channel count and the exact device id RemSound uses internally. This is the quickest way to confirm an interface is actually present and seen by Windows.
`--selftest`
(or `--smoke-test`)| Runs RemSound's built-in self-test and reports **PASS** or **FAIL**. It works through a list of named checks: a full audio round-trip on the machine on its own (capture → encode → send across the network layer to itself → receive → decode, for both quality settings), the audio encryption, the network packet format, saving and reloading settings and a profile, that a diagnostics report never leaks a password, and that the bundled sounds and manual are present. No sound is played out, so it is safe to run silently. Add `--seconds N` to make the audio part run for longer than the default.
`--perftest`
[`--seconds N`]| Runs the audio path through several short cycles and reports whether RemSound's handle, memory and thread use stays bounded — a quick check for resource leaks. Prints the numbers each cycle so two builds can be compared. No sound is played out.
`--diagnostics`| Writes a single plain-text report file holding the version, the operating system, the current settings, the list of profiles, the full device list, a check of the Windows microphone-privacy permission, a quick live audio self-check, the most recent session snapshot and recent warnings from the log, and the tail of the most recent log. With no path it saves into the **user settings and logs** folder and prints where it put it; you can also give a path, for example `--diagnostics C:\Users\you\Desktop\report.txt`. This is the file to send when asking for help — it answers most questions in one go.

### Options that change a setting or control a running copy, then exit

Option| What it does
---|---
`--log on` or `--log off`| Turns the diagnostic log on or off. The change takes effect the next time RemSound starts. The same setting lives in the Preferences dialog; this is just a way to set it without opening the window.
`--close`| Closes a copy of RemSound that is already running. Useful in a script that needs to restart it.

### Options that change how RemSound starts

These open RemSound as normal, set up the way you ask, and are meant for shortcuts and scripts.

Option| What it does
---|---
`--profile "<name>"`| Starts straight into the named profile and skips the profile picker. Put the name in quotes if it contains a space, for example `--profile "Studio link"`.
`--connect <ip>`| Starts and connects to a peer at that address. You can give just an address (`--connect 192.168.1.42`) or an address and port (`--connect 192.168.1.42:47830`); with no port it uses RemSound's normal port, 47830. If you don't also give a `--profile`, it starts on a fresh blank profile already pointed at that peer.
`--minimized` or `--tray`| Starts minimized to the notification area, with no window popping up. Pair it with `--profile` or `--connect` so it has something to do without waiting at the picker.
`--config-dir <folder>`| Uses an explicit folder for this run's settings, profiles, logs and sounds, instead of the usual location. It lets you (or an automated test) run RemSound against a throwaway folder without touching your real settings. Works with any command — for example `--selftest --config-dir C:\Temp\rstest` or `--diagnostics --config-dir C:\Temp\rstest`.

### Examples


    RemSound.exe --devices
    RemSound.exe --selftest --opus
    RemSound.exe --diagnostics
    RemSound.exe --profile "Studio" --minimized
    RemSound.exe --connect 192.168.1.42


A common support sequence: ask the person to run `--diagnostics` and send you the file, then have them run `--selftest` — if that says PASS, capture, encoding and the audio path are all sound on their machine and the problem is somewhere in the connection between you.

## 24. The lock-screen service (send only)

Normally RemSound runs as an app you open and close. The **lock-screen service** is a second, silent way to run it: a Windows service that keeps _sending_ this machine's audio to your peers even when the screen is locked, when you have signed out, and when nobody is logged in at all (for example after an unattended restart). It is useful when a machine needs to stream its audio continuously without someone sitting at it.

It is deliberately limited:

  * **Send only.** The service captures and sends; it never plays received audio. (Windows silences audio output when no one is logged in, so receiving on the lock screen is not possible.) Use the normal app for listening.
  * **WASAPI only.** ASIO devices cannot be used by a Windows service. If you need ASIO, use the normal app.
  * **It gets out of your way.** Whenever you open the normal RemSound app, the service automatically stops sending and hands over to you — so you are always in control while you are at the machine. When you close the app (or it crashes, or you sign out), the service takes over again a couple of seconds later, re-reading its profile so any changes you made are picked up. You never need to stop it by hand to make a change.



### Setting it up

Everything lives in the **Service** menu on the menu bar:

  1. **Configure service profile …** — opens a small window with two tabs (Connectivity and Audio send) where you choose who to send to (plus a password) and what to send. On the Audio send tab, the first output choice is **Use Windows default audio device, follows Windows changes** — tick that to send whatever this machine is currently playing and keep following the Windows default if it later changes, rather than pinning one named card. (You can still pick specific devices, or a specific application, exactly as in the normal app.) There is no “send my audio” switch because the service always sends, and there is no audio-quality tab to fiddle with: the service always uses the settings that work best for live streaming (the Opus live-latency codec, small packets, locked to the audio clock), so it just sounds right. The service password follows the same strength rule as the app (from version 5.6): because the service runs in the background with no window, it can't ask you to strengthen a weak one — instead it simply won't stream, and RemSound warns you on its next launch (and when you save the service profile) so you know to change it here. This is a separate profile from your normal ones and does not appear in the usual profile list. The **Additional options** button holds two extras. First, a switch for the service's own log. Second, **Set the machine's volume when the service starts** : tick it, pick a volume percent, and the service unmutes the machine and sets its Windows volume to that level when it starts — handy for an unattended PC that booted muted or was left turned down, so it's audible again with nobody at the keyboard. The **When** list chooses between _Only the first start after each boot_ and _Every time the service starts_. **First start after boot is the recommended, set-and-forget choice** : it sets the volume once when the machine boots and never touches it again, so it won't fight you while you're using the machine. _Every time the service starts_ re-applies on every service start — useful if you deliberately restart the service to reset the volume, but be aware the service also restarts by itself for routine reasons (installing a RemSound update, saving the service profile), and this mode re-applies on those too. To stop it ever machine-gunning your volume, either mode skips a re-apply if the volume was already set within the last few minutes. Changes take effect from the service's next start, no reinstall needed. (The service itself never plays sounds — it streams silently in the background — so there are no cue options here.)
  2. **Install service** — registers it with Windows so it starts automatically at every boot. Windows asks for administrator permission (one prompt). Do this once. Straight after installing, RemSound asks whether you'd like to **start it now** (otherwise it waits until the next reboot). (When you first install RemSound on a PC, the app installer also offers to set the service up — and start it — for you, so you may have done this already.)
  3. **Start service** / **Stop service** — run or halt it now without waiting for a reboot.
  4. **Uninstall service** — removes it entirely.



The top of the Service menu always shows the current state: not installed, installed and running, or installed and stopped.

### Good to know

  * The service sends to the exact peer addresses you list in its profile (a computer on your network, or a reachable address across the internet). Automatic peer discovery and hole-punching are handled by the normal app, not the service.
  * Because the service runs even when you are not logged in, it sends from the system account. If Windows Firewall ever prompts about RemSound, allow it so the audio can get out.
  * When RemSound updates itself, the service updates itself too, automatically — it notices the newer version, copies it in and restarts onto it on its own, with no prompt and nothing for you to do. (This happens on the service's own schedule shortly after the app updates; nothing breaks in the meantime.)
  * If two computers should each stream to the other unattended, install the service on both.



## 25. Troubleshooting

### I don't hear my friend

  1. Connectivity tab: is your friend in **Connected peers** with a healthy round-trip time (for example “192.168.1.5: 27 ms”), not _unreachable_ or _pending_?
  2. Audio inputs and outputs tab: is **Receive audio** ticked?
  3. Same tab: is at least one output device ticked, in the WASAPI or the ASIO output list?
  4. Same tab: is the volume slider above zero?



### My friend doesn't hear me

  1. Audio inputs and outputs tab: is **Send my audio** ticked?
  2. Same tab: is at least one capture source ticked across the three send lists?
  3. If you're using a microphone: is Windows allowing apps to use it? RemSound now pops up a warning when you switch on a microphone Windows is blocking — including when a profile loads with one already on — but to check by hand, open Settings → Privacy & security → Microphone and make sure both _Microphone access_ and _Let desktop apps access your microphone_ are on. (When Windows blocks it the mic sends silence rather than failing, so it's easy to miss.)
  4. Have they ticked _your_ name in their Discovered peers list?



### I can hear them but the sound crackles

  * **First, try raising Buffer smoothness** by 1 or 2 on the Audio profile tab (Alt+B). It usually fixes crackles for a smaller delay cost than raising the latency does.
  * Then, try raising the Audio latency value (Alt+L). Internet connections often need 30–80 ms.
  * If you're using PCM, switch to Opus — Opus can repair single missing packets automatically, which PCM can't. It's much more tolerant of an unsteady network.
  * If you're on Wi-Fi, try wired Ethernet — Wi-Fi adds 5–20 ms of unpredictable jitter.
  * If you previously set Packet size to “Small (LAN only)” but you're now on the internet rather than a same-house network, switch it back to Standard. Small packets double the packet rate, which makes internet clicks more likely.



### The sound is fine but the delay feels long

  * Lower the relevant latency value on the Audio profile tab, step by step, until clicks just start, then nudge it back up by one step. With an ASIO driver chosen, this is two separate controls (one for each path).
  * Or, turn on Continuous auto-tune for the path that feels slow and let RemSound find the right level.
  * Lower Buffer smoothness towards 3 if you'd been running it high “just in case”.
  * If only WASAPI matters for your session, set the ASIO driver picker to _(none)_ — that turns ASIO off entirely.



### The ASIO sound is grainy or constantly micro-clicks

Most likely your ASIO latency target is below the network's real-world jitter level. The receiving side fights to hold the reserve at the target, and that fight is audible. Raise **ASIO latency in milliseconds (Alt+L)** on the Audio profile tab to 25 ms or more and the graininess should disappear. Even pure-ASIO setups can't safely sustain a receive reserve below about 15 ms over real networks; aim higher on Wi-Fi.

### I can't see my friend in Discovered peers

  * If you're on the same local network: are both computers on the same Wi-Fi or Ethernet network? Some guest networks deliberately keep devices from seeing each other.
  * If you're using Tailscale: type their Tailscale address into “Add peer by IP” (Connectivity tab, Alt+A) once. After that, both sides see each other automatically.
  * Check that Windows Firewall isn't blocking RemSound. The first launch usually asks; if you said no, you'll need to allow it by hand.



### A peer rebooted or changed address and the sound didn't come back

RemSound keeps trying the address you connected to, so when a peer comes back at the same address — the usual case after a reboot — the sound returns on its own within a few seconds, with nothing for you to do. If the peer comes back at a _different_ address (a new DHCP lease, say), reconnect to it: pick it again from **Discovered peers** , or enter its new address with **Add peer by IP** (Connectivity tab, Alt+A). If the sound still doesn't return, the peer is genuinely unreachable — off, asleep, or a firewall is blocking the path.

### RemSound closed unexpectedly

RemSound is normally very stable, but if it ever closes on its own, it writes a small crash file into your logs folder (the RemSound folder → **user settings and logs** → **logs** , named `crash-` followed by the date and time). There's nothing you need to do with it — but if it happens, sending that file along with your report records what went wrong, so it can be tracked down and fixed.

### One side says “unreachable” even though sound is flowing

The health check-ins use the same channel as the audio, so if the sound gets through, the check-ins should too. If one side shows “unreachable” while the sound plays fine, make sure both computers are running the same version of RemSound — an older version on either end can speak a slightly different check-in language.

### My other audio went silent or crackly when I selected an ASIO driver

You probably picked Realtek ASIO. It's a generic driver, not tied to Realtek hardware, and it tends to grab whatever Windows treats as the default sound device — usually the same one your screen reader is using. Set the ASIO driver picker back to _(none)_ , or pick a different ASIO driver.

### The device list shows old devices that are no longer plugged in

RemSound reacts the moment a device is plugged in or unplugged, so an unplugged device should disappear within a second or two. If one lingers, restart RemSound — Windows' own device list occasionally needs a nudge.

### No sound after the computer wakes from sleep

RemSound notices when the computer has just woken up, waits a moment for any USB sound devices to come back to life, and rebuilds its audio engine from scratch — you'll briefly see a small “Reconnecting to audio driver” window during the rebuild, then sound should resume on its own. If sound still doesn't come back, click on the ASIO driver picker on the Audio inputs and outputs tab and re-pick the same driver (or pick _(none)_ and then re-pick your driver). That triggers the same full rebuild manually. As a last resort, quit and reopen RemSound.

### A sound card you were listening through was unplugged

If a sound card you're playing received audio through is unplugged and then plugged back in, RemSound now re-opens it on its own and the sound resumes — you don't have to re-tick it in the output list. This works when the card comes back as the same Windows device, which is the usual case when you plug it into the same socket. If you move it to a different USB socket and Windows treats it as a brand-new device, just tick it again in the output list.

### UPnP says “no router found” even though my router supports it

The most common reasons:

  * UPnP is disabled in your router's settings. Look for a checkbox marked “UPnP”, “NAT-PMP”, or “Allow apps to automatically forward ports” in the router's admin page. It's often off by default.
  * Your Windows network is set to “Public” rather than “Private”. Public mode blocks the discovery messages RemSound uses to find the router. In Windows' network settings, switch your home network to Private.
  * Your computer is on a Wi-Fi guest network or a corporate / hotel network. These networks usually block the kind of discovery messages UPnP needs.



If none of those apply, just fall back to Tailscale — it works without involving the router at all.

## 26. Glossary

Term| Meaning
---|---
WASAPI| The normal Windows way of handling sound. Every speaker and microphone in your Windows sound settings works this way. The delay it adds is around 10–30 ms.
ASIO| A faster, more direct way of handling sound, used by professional audio equipment. It talks straight to the hardware, giving a delay of under 5 ms. It only works if your audio interface came with an ASIO driver.
Loopback capture| Capturing what's currently being played out of an output device, rather than what's coming in from a microphone. The “WASAPI audio outputs to send” list does loopback capture.
Channel pair| A stereo pair of channels on an ASIO driver. Pair 1 is channels 1 and 2, Pair 2 is channels 3 and 4, and so on.
Codec| The method RemSound uses to package the sound before sending it. RemSound offers PCM (no compression) and Opus (compressed).
Opus| A high-quality codec that compresses sound to use much less network bandwidth, and can repair single lost packets on its own. The right choice for internet connections.
PCM| An uncompressed codec — the very best quality with no loss at all, but it uses far more network bandwidth than Opus. Best on a local network or a fast connection.
FLAC| A file format for recordings that compresses the sound with no loss of quality — the file plays back identically to an uncompressed WAV, but is about half the size.
Latency| The small delay between sound leaving one computer and arriving at the other.
Jitter| When network packets arrive unevenly instead of in a steady stream. This is why a reserve of sound is kept on the receiving side.
Peer| Another computer running RemSound that you're connected to or want to connect to.
Heartbeat| A small message that connected computers exchange every second to confirm they can still reach each other and to measure the round-trip time. It travels on the audio channel (47830 by default).
Discovery| The way RemSound computers find each other on the network without you having to know each other's addresses up front.
Tailscale| An easy-to-use VPN that puts your computers on a private network together. The simplest way to connect RemSound across the internet without changing your router settings.
UPnP| Short for “Universal Plug and Play”. A feature most home routers support that lets an app politely ask the router to open a port for incoming connections, without the user having to log into the router. RemSound uses UPnP (and its newer relatives NAT-PMP and PCP) to set up port forwarding automatically when you tick “Automatically open my router for incoming connections” in Preferences. Off by default.
NAT| Short for “Network Address Translation”. The way your router lets several computers share a single internet connection — one public address on the outside, lots of private addresses on the inside. Most home networks use NAT, which is why you usually need port forwarding (or UPnP, or a VPN) for two computers in different places to reach each other directly.
Carrier-grade NAT| An extra layer of NAT that some internet providers (especially on mobile broadband and some cable connections) put in between your router and the rest of the internet. Your home router opens a port fine, but the provider's NAT in front of it still blocks incoming connections. RemSound's UPnP status line warns you when this is the case — the way through it is a VPN like Tailscale, or the relay server.
Auto-tune| RemSound automatically adjusting the latency target based on how evenly packets are arriving. Off by default; turn it on with the Continuous auto-tune checkbox on the Audio profile tab.
Profile| A saved snapshot of every RemSound setting and choice — device ticks, send / receive states, codec, latency, peers, ASIO driver choice, the lot. (Keyboard shortcuts are the exception — they're shared across all profiles, not saved per profile.) Stored as one settings file. You pick one at startup, and can switch with File → Open profile.
New profile| An entry in the startup profile picker that begins a session with all the defaults — nothing ticked, no peers, no saved name. A clean starting point for a new profile, or for a one-off session you don't plan to save.
Lock to audio clock| A sending-side timing mode that takes its timing straight from the sound device's hardware clock instead of from Windows. Removes a few milliseconds of wobble at tight latency targets. RemSound now does this always — it used to be a checkbox on the Audio profile tab, but it is on permanently and no longer a setting.
Concealment| A receiving-side feature that fills brief gaps in the playback reserve with a small noise burst (the default) or an obvious click. You choose which on the Audio profile tab, in the _Artefact sound type_ list. Opus also has its own repair of lost packets on top of this.
Remote control| A RemSound feature that lets one connected peer adjust another peer's listening volume (or toggle their receive mute) using global hotkeys. There are two sets of commands: one adjusts the receiver's RemSound volume slider, the other adjusts the receiver's Windows volume. Off by default on both ends; the receiver opts in via “Accept remote volume commands from peers” in the Preferences dialog (Ctrl+P), and the sender sets up hotkeys in the Keyboard shortcuts dialog (Ctrl+K). Designed for the “I'm NVDA-Remote'd into my desktop and want to nudge the laptop's volume” case. See Remote control.

* * *
