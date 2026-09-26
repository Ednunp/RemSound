# RemSound — user manual

RemSound is a Windows program that sends live sound from one computer to another, with very little delay. Picture a private audio link between two or more computers: each one decides what sound it wants to send and what sound it wants to play, and the audio travels straight between them over your network.

I built RemSound because I work on a powerful computer from a lighter one, and I wanted to hear the powerful machine's sound on the laptop in front of me. Other programs move sound between PCs; none of them did it the way I wanted.

It turns out to be just as useful for listening to one room from another, playing music together over the internet, co-hosting a podcast, or anything else where sound has to get from one PC to another quickly.

## Context help (F1)

Press **F1** anywhere in RemSound for **context help** : help on the control you're on. The context help window opens and reads out what that control does, in the words of this manual. The arrow keys go through it line by line. **Open the manual in your default browser** (Alt+O) opens this manual in your web browser at that same place. **Escape** closes context help and puts you back on the control. RemSound plays a short sound as context help opens and another as it closes; you can change either one, or turn it off, on the Audio cues tab of Preferences.

Press **Shift+F1** anywhere to open this whole manual in your default web browser. **Help** on the Help menu does the same.

When RemSound starts, a message tells you that context help is available. Tick **Do not show me this message again** on it to stop it. To bring it back, tick **Enable context help message at startup** on the Startup behaviour tab of Preferences.

## Table of contents



  1. [Context help (F1)](#context-help-f1)
  1. [What RemSound does](#1-what-remsound-does)
  1. [Quick start](#2-quick-start)
  1. [Profiles](#3-profiles)
  1. [The main window: menu bar and tabs](#4-the-main-window-menu-bar-and-tabs)
  1. [Menus (File, Record, Service, DAW plugin, Options, Help)](#5-menus-file-record-service-daw-plugin-options-help)
  1. [Connectivity tab (main window)](#6-connectivity-tab-main-window)
  1. [Audio inputs and outputs tab](#7-audio-inputs-and-outputs-tab)
  1. [Audio profile tab](#8-audio-profile-tab)


## 1. What RemSound does

RemSound carries sound from one PC's microphone or sound card to another PC's speakers or audio interface, almost instantly, over your network. Both computers run the same program, and each one decides for itself whether it wants to send sound, receive sound, or do both.

### The basic flow

Step| What happens
---|---
1| You tick “Send my audio” on the Audio inputs and outputs tab and choose which microphone or sound output you want sent.
2| Your friend ticks “Receive audio” on the same tab and chooses which speakers or headphones should play the sound they receive.
3| One of you ticks the other person in the Discovered peers not on a server list on the Connectivity tab (or types their address by hand).
4| Sound starts flowing. The other direction works exactly the same way, on its own — both of you can speak at the same time.

There is no account and nothing stored online. The sound goes straight from one computer to the other — or, if you run a server of your own, through that.

> **RemSound on Android (receiver):** there is a companion app that lets a phone or tablet _receive_ RemSound audio — handy for listening on the move. It's a separate community project built and maintained by Aryan Choudhary, who is a screen-reader user himself and has tuned the app for TalkBack; it is not part of RemSound and is not maintained by us. Get the signed app from its releases page (download the latest **app-release.apk**): [RemSound Android — Releases](https://github.com/aryanchoudharypro/RemSoundAndroid/releases).

> **RemSound on iOS (beta):** there is also a companion app for iPhone and iPad, currently in beta testing on Apple's TestFlight. Like the Android app it's a separate community project — built and maintained by Jonathan Schuster — and is not part of RemSound and not maintained by us, but it speaks the same protocol. Join the beta here: [RemSound for iOS on TestFlight](https://testflight.apple.com/join/pNCnj3z2).

## 2. Quick start

> **If RemSound won't start** — if Windows says it needs “.NET” — RemSound runs on the **Microsoft .NET 10 Desktop Runtime**. Most up-to-date Windows machines already have it; if yours doesn't, open the **Install and Uninstall Scripts** folder sitting next to `RemSound.exe` and double-click **Install .NET for RemSound.cmd**. It fetches and installs it for you (using winget, the Windows package manager), and then RemSound will start. There's a `.ps1` version in the same folder for PowerShell users. If you'd rather do it by hand, the script opens Microsoft's download page, where you pick “.NET Desktop Runtime” for “x64”.

Let's assume you and a friend both have RemSound running, and that your two computers can reach each other on the network (the same Wi-Fi, the same Tailscale account, and so on).

  1. Start RemSound. The first thing you'll see is the **profile picker**. On a brand-new install your only choice is **New profile** — select it and press Enter or click OK. Later, once you've saved a setup or two of your own, this is the dialog where you choose which one to load. See Profiles for the full story.
  2. Agree a password with your friend. The first time you tick **Receive audio** or **Send my audio** , RemSound asks for one. Type the password you agreed. It must be exactly the same on both computers, or no sound passes between you. See Passwords and encryption.
  3. Once the main window opens, go to the **Audio inputs and outputs** tab. Tick **Receive audio (Alt+R)** , then tick the device you want incoming sound played through in **WASAPI outputs for received audio (Alt+3)**.
  4. On the same tab, tick **Send my audio (Alt+S)** and tick your microphone in **WASAPI audio inputs to send (Alt+5)**.
  5. Go to the **Connectivity** tab, find your friend in the **Discovered peers not on a server (Alt+D)** list, and tick them. If they aren't showing up, use **Add peer by IP (Alt+A)** and type their address.
  6. Have your friend do the same with you on their computer.
  7. Within a second or two, both of you will hear each other.
  8. In the **File menu** (Alt+F), choose **Save as** to give your setup a name. Next time you start the program, picking that name from the startup dialog restores all your settings, device choices, peers, and connections in one go.



> **If you have a professional audio interface (Audient, Komplete Audio, RME, Focusrite, and the like):** on the **Audio inputs and outputs** tab pick your driver in the **ASIO driver (Alt+D)** list to use its low-delay channels. Choosing a real driver makes the ASIO device lists appear; choosing _(none)_ hides them again and the app uses the ordinary Windows sound path only. See ASIO and WASAPI below.

## 3. Profiles

RemSound saves your whole setup — which devices are ticked, whether you're sending or receiving, your sound quality settings, delay targets, your ASIO driver choice, the peers you’re connected to, and the server you were on and who you had ticked there — into a single settings file. Each saved setup is called a _profile_. You choose which profile to load every time you start the program. You might keep one profile for “morning podcast” and another for “evening jam session”, each with a different mix of devices ticked, and switch between them in a couple of clicks. (Your keyboard shortcuts are the one thing that _isn't_ saved per profile — they're shared across all your profiles; see Global hotkeys.)

### The startup picker

When RemSound starts, the first thing you see is the profile picker. It is a list of your saved profile names, with an extra entry called **New profile** at the top. The keys are deliberately simple:

Key| Action
---|---
Profiles list (Up / Down)| Your saved profiles, with **New profile** at the top for a fresh session with all the defaults. A locked profile has “(read-only)” after its name. Arrow to one and press Enter to load it and open the main window, or press Del to delete it.
Enter| Load the highlighted profile (or a new profile) and open the main window.
OK button (Alt+O)| Loads the highlighted profile and opens the main window; on **New profile** , it starts a fresh session with all the defaults. Enter on the list does the same. If a profile can't be read, RemSound says so and starts a new profile instead.
Del, or the Delete button (Alt+D)| Deletes the highlighted profile, after a yes / no question that starts on No. It can't be undone. The **New profile** entry can't be deleted.
Browse for profiles folder… button (Alt+B)| Choose a different folder to read profiles from. Handy if you keep your profiles in Dropbox or another sync folder so they follow you between computers. Your choice is remembered next time RemSound starts.
Reset to default folder button (Alt+R)| Go back to the usual profiles folder inside RemSound, after you've used Browse.
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
**Rename the current profile**|  File → Rename current profile (Alt+F, M). It asks for the new name and renames the profile everywhere: its file, the name the lists show, and the window title. If it's the profile RemSound starts with, or in Recent profiles, those follow the new name.
**Delete a profile**|  File → Open profile, then right-click the entry in the Windows file picker and choose Delete. RemSound lets Windows handle this rather than having its own delete button.

#### The profile name box

Control| Shortcut| What it does
---|---|---
**Profile name** (when renaming, **Please enter a new name for your profile**)| —| The name for the profile. RemSound asks for one when you save a new, unnamed session before switching profile, starting a new one or closing RemSound, and when you choose File → Rename current profile, where the box starts with the current name. Enter here does the same as OK.
**OK**|  Alt+O, or Enter| Uses the name in the box: saves the profile under it, or renames the current profile to it. An empty box asks you to type a name. When saving, a name another profile already has asks whether to overwrite that profile; when renaming, RemSound says the name is taken and renames nothing.
**Cancel**|  Alt+C, or Escape| Closes the box without saving or renaming. If you were saving before switching profile, starting a new profile or closing RemSound, that is called off too: you stay where you were, with your changes still there.

### Where your files are stored

Everything RemSound keeps for you on this computer — your settings, your profiles, and your logs — lives together in one folder inside RemSound called **user settings and logs**. Each profile is one small file, stored at:


    <RemSound folder>\user settings and logs\profiles\<your computer name>\<profile name>.json

RemSound updates never touch that folder — so anything of your own in there stays safe when you update.

The built-in cue sounds are kept separately, in a **default sounds** folder alongside the program. Those are part of RemSound itself, so an update can refresh them — if a future version ships an improved default sound, you'll get it. Your own choices are never affected: a sound you pick for a cue with the **Browse** button is remembered as a link to your own file (wherever you keep it), and that's left exactly as you set it.

The folder named after your computer keeps each machine's profiles separate. If you used the **Browse …** button on the startup dialog to pick a different folder (for example, one inside Dropbox), the profiles are stored directly in that folder — with no per-computer subfolder — so two computers pointed at the same shared folder see exactly the same list.

You can also **copy a profile file from one computer to another** : drop it into the other computer's profile folder and it will appear in that computer's startup dialog. If the other computer doesn't have the same equipment (different sound cards, different ASIO drivers), those device choices are skipped when the profile loads — RemSound won't show an error or a warning, the relevant lists just won't have those items ticked. Saving the profile on that computer keeps those ticks, so the profile still works when you copy it back.

> **Tip:** profile files are plain text and readable by people. If you ever want to change something by hand (for example, a remembered peer) without opening the app, you can open the file in any text editor.

### What is NOT saved in a profile

A few things are deliberately kept out of profiles:

  * **The folder profiles are read from.** You choose this with the Browse… button on the startup dialog. It's kept in a small settings file on that particular computer.
  * **Live connection health figures** — these describe what's happening right now, not your setup.
  * **Anything auto-tune has learned** — this is worked out fresh each session.
  * **Window position and size** — Windows itself remembers these.
  * **Your Preferences** — the colour theme, updates, logging, who may connect to you and the rest of Options → Preferences are kept on this computer, and apply whichever profile you load. Two exceptions travel with the profile: **Accept remote volume commands from peers** , and eight of the cue sounds (see Which cue settings travel with the profile).
  * **Your keyboard shortcuts, your named peers, and the remembered peers, servers and applications lists** — these are kept on this computer and shared by all your profiles.



### Locking a profile (read-only)

By default, RemSound treats your profile like a document: if you change something while it's running, you'll be asked “save changes?” when you exit. Most of the time that's exactly what you want — you don't lose work by accident.

But sometimes you want the opposite. You have a profile you live in every day, you toggle send or receive on or off during the day as a matter of course, and you don't want to be asked about saving every single time you close RemSound. You especially don't want to be asked if RemSound might close itself for some other reason (a Windows update, your screen reader crashing, a remote session dropping, a laptop going into hibernate) — because then there's a save prompt sitting on screen that nobody can dismiss, and the app can't actually close.

**Locking the profile** solves this. When ticked:

  * The profile loads normally and everything in the app works the same way it always did.
  * Anything you change during the session — ticking a device, sliding the volume, toggling send or receive, picking a peer — **still works for that session**. RemSound just doesn't write any of it back to the profile file on disk.
  * When you close RemSound, there is **no save prompt**. The app just closes. Whatever you changed during the session is forgotten; the next time you open the profile it's back to what it was when you locked it.
  * The window title shows “(read-only)” so you can always tell at a glance.
  * The periodic auto-save leaves it alone too. That is the point of the lock: nothing is written back to a locked profile unless you save it on purpose. If you change something on a locked profile and expect it to still be there tomorrow, unlock it first.
  * The startup profile picker also shows “(read-only)” next to locked profiles, so you know what you're picking before you hit Enter.
  * Pressing Save (Ctrl+S) still works — the lock only blocks the automatic save prompt, not deliberate saves. See “Saving on purpose while a profile is locked” below.



**How to lock or unlock:** open the File menu (Alt+F) and pick **Lock profile (read-only)** (Alt+F, L). It's a tickable menu item — pick it once to turn the lock on (a tick appears next to it); pick it again to turn the lock off (the tick disappears). The lock state is remembered with the profile, so closing and reopening RemSound keeps the profile locked exactly as you left it.

#### Saving on purpose while a profile is locked

The lock is there to stop accidents — it doesn't stop you saving when you mean to. If you press **Save** (Ctrl+S) or pick **File → Save** on a locked profile, RemSound shows a one-time warning explaining what's about to happen:

> **Saving onto a read-only profile.** You're about to save changes onto a profile that's marked as read-only. RemSound allows this because you asked to save on purpose — the lock only stops the automatic “save your changes?” prompt; it doesn't stop you saving when you mean to.
>
> Click **Save anyway** to overwrite this profile, or **Cancel** and use File → Save as… if you'd rather save your changes to a new profile.

There's a **Do not show me this message again** tick on the warning. Once you tick it, future deliberate saves on a locked profile go through silently without the warning. The setting is per-machine, not per-profile — tick it once and it applies on every locked profile from that point on.

On a locked profile:

  * Closing RemSound — no prompt, changes are forgotten.
  * Switching to a different profile — no prompt, changes are forgotten.
  * Pressing Save (Ctrl+S) on purpose — warning the first time (with a do-not-show-again tick), then the save goes through and overwrites the profile.
  * **Save as …** — always works, never warns. The new copy starts out unlocked.



> **If a save prompt is blocking your shutdown right now:** close it by pressing Esc (or click Cancel if you can see it), unlock by going File → Lock profile (read-only), then close RemSound. From this launch forward there'll be no prompt.

## 4. The main window: menu bar and tabs

The main window has three parts, stacked top to bottom:

  1. A **menu bar** at the top with six menus — _File_ , _Record_ , _Service_ , _DAW plugin_ , _Options_ and _Help_. See Menus.
  2. A **row of tabs** — Connectivity, Audio inputs and outputs, Volume, pan and EQ for peers, and Audio profile — so four by default. The Volume, pan and EQ tab can be hidden, and you can reorder or hide any of the tabs (and jump straight to one with Ctrl and its number) from the **Appearance** tab in Preferences. Each tab has its own Alt+letter shortcuts that only work when that tab is the one showing — so the same letter can do different things on different tabs without clashing.
  3. A **status line** at the bottom that updates once a second with how long you've been connected, how many peers you have, whether sound is flowing, connection health, and RemSound's own CPU and memory usage.

Tab| What it's for
---|---
**Connectivity**|  Connected, discovered and remembered peers. Connecting to a server, and the people on it. Adding a peer by address. A connection status read-out.
**Audio inputs and outputs**|  The ASIO driver picker (when an ASIO driver is installed), the Receive audio and Send my audio checkboxes, and all the device lists. Choosing a real driver in the picker brings up the ASIO device lists alongside the ordinary Windows ones; choosing _(none)_ hides them.
**Volume, pan and EQ for peers** (optional)| Shape each connected peer's sound on its own — their volume, pan (left/right) and EQ. Shown by default; untick “Show the volume, pan and EQ for peers tab” on the Appearance tab of Preferences to hide it. See Volume, pan and EQ for peers tab.
**Audio profile**|  Codec, packet size, jitter buffer, continuous auto-tune, buffer smoothness, artefact sound. Split into an _Audio send parameters_ group and an _Audio receive parameters_ group.

### The system tray icon and its menu

When RemSound is **minimised to the tray** (via **File → Minimise to tray**, the “Show or hide window” global hotkey, or by starting minimised on launch), the main window hides and an icon appears in your Windows system tray (the small icons cluster next to the clock).

**Hovering over the tray icon** shows a short summary of what RemSound is doing right now — how many people you're connected to (each person on a server counts as one), whether you're sending or receiving and in which mode (WASAPI, ASIO, or both), and whether a recording is running. The summary keeps itself up to date as things change. It tells you a recording is in progress, but not its exact length — for that, glance at the main window. Examples:

  * _RemSound — not connected_
  * _RemSound, 2 peers, sending (WASAPI), receiving (WASAPI)_
  * _RemSound, recording, 1 peer, sending (WASAPI + ASIO), receiving (WASAPI + ASIO)_



**Right-clicking the tray icon** opens a small menu with everything you might want to reach without re-opening the main window:

Item| Shortcut| What it does
---|---|---
**Show RemSound**|  W| Brings the main window back to the front and gives it focus. Double-clicking the tray icon does the same thing.
**Enable sending** (tickable)| S| Toggles “Send my audio” on or off, the same way as the checkbox on the Audio inputs and outputs tab. The tick reflects the current state — ticked means sending, unticked means not.
**Enable receiving** (tickable)| R| Toggles “Receive audio” on or off, the same way as the checkbox on the Audio inputs and outputs tab. The tick reflects the current state — ticked means receiving, unticked means not.
**Profiles →**| P| A submenu listing your recent profiles, most recent first. Each row has a single-digit shortcut: while the submenu is open, press **1** for the most recent, **2** for the next, and so on up to **5**. Selecting one switches the active profile, exactly the same way as the File menu's Recent profiles submenu. Greyed out as “(No recent profiles)” when you haven't loaded any yet.
**Exit**|  X| Closes RemSound entirely.

**Keyboard access:** the tray icon is reachable through standard Windows shortcuts — **Windows + B** moves focus to the notification area, arrow keys navigate, Enter activates, and the application context-menu key (or Shift+F10) opens the right-click menu without a mouse.

**Important warnings always come to the front.** Even when RemSound is hidden in the tray, a warning it needs you to read — such as a microphone-blocked warning or an update prompt — pops up in front of whatever you're doing, with focus, so your screen reader reads it straight away. RemSound stays in the tray; only the warning comes forward.

### Only one copy of RemSound runs at a time

RemSound only ever runs as a single copy. If you try to open it while it's already running — for example by double-clicking it when it's already sitting in the system tray — it won't start a second one. Instead it asks what you'd like to do:

  * **Switch to the running copy** — brings the copy that's already running back to the front. This is the default and usually what you want. Remember it may be minimised to the system tray, down by the clock.
  * **Force the running copy to close and start fresh** — only needed if the running copy is stuck or not responding. It closes that copy and starts a new one. If the stuck copy was in the middle of a recording, that recording is lost — so this is the “get me out of trouble” option, not an everyday one.
  * **Cancel** — do nothing.



## 5. Menus (File, Record, Service, DAW plugin, Options, Help)

There are six menus on the main window: **File (Alt+F)** , **Record (Alt+K)** , **Service (Alt+J)** , **DAW plugin (Alt+G)** , **Options (Alt+O)** and **Help (Alt+H)**.

Three of those shortcut letters look odd, and all three are deliberate. A checkbox on the main window beats a menu for the same Alt key, so Record uses **K** because Alt+R belongs to _Receive audio_ , Service uses **J** because Alt+S belongs to _Send my audio_ , and DAW plugin uses **G** because Alt+D belongs to the _Discovered peers not on a server_ list. Each menu shows its own shortcut in its title, so you can always see which letter opens it.

### File menu

The File menu holds everything to do with profiles — opening, saving, renaming — plus minimising to the tray and exiting.

Item| Shortcut| What it does
---|---|---
**New profile**|  Ctrl+N, or Alt+F, W| Starts a brand-new profile from scratch — a fresh, unsaved session (everything unticked, nothing connected, default settings). This is how you create a profile for a different setup at any time, _even when RemSound is set to start straight into a specific profile and you never see the picker_. If your current profile has unsaved changes, it offers to save them first. Once you've set things up, use Save as to give the new profile a name.
**Open profile …**| Ctrl+O, or Alt+F, O| Opens a Windows file picker showing your profiles folder. Pick a profile, and RemSound reloads using it (the window closes and reopens with all that profile's device choices, peers and settings restored). If your current profile has unsaved changes, it first asks whether to save them: Yes saves and switches, No switches without saving, Cancel stays where you are. To delete a profile, right-click its entry inside the file picker and choose Delete — that lets Windows handle the deletion. If the profile you pick can't be read, RemSound says so and opens a new, blank profile instead, and your profile file is left exactly as it was.
**Recent profiles →**| Alt+F, R| A submenu listing the last five profiles you've opened, most recent first. Each row has a single-digit shortcut: while the submenu is open, press **1** for the most recent, **2** for the next, and so on up to **5**. Or just select the one you want. It reloads the profile the same way Open profile does, and asks about unsaved changes the same way. If a recent profile's file has been deleted or moved away, it's left out of the submenu (it stays in the list in case the file comes back later — for example when you reconnect an external drive). If the list is empty, you see a greyed-out “(No recent profiles)” entry. The same list appears in the system-tray icon's **Profiles** submenu, with the same number shortcuts, so you can switch profiles without re-opening the main window.
**Save**|  Ctrl+S| Updates the current profile with your current settings. If there's no current profile (you're on a new profile), this becomes Save as automatically.
**Save as …**| Alt+F, A| Asks for a name and saves a copy. Use it to save your current setup under a new name, or to save for the first time from a new profile.
**Rename current profile …**| Alt+F, M| Renames the current profile: its file, the name every list shows it by, and the window title. If it's the profile RemSound starts with, or in Recent profiles, those follow the new name too. Changes you haven't saved stay unsaved. Does nothing on a new profile (there's no profile to rename).
**Lock profile (read-only)** (tickable)| Alt+F, L| When ticked, the current profile is loaded for use but RemSound will not save any of your changes back to it. The window title shows “(read-only)” so you can tell at a glance. Closing RemSound never asks “save changes?” — it just closes. Save (Ctrl+S) still works if you mean it: it warns you once, then saves. Anything you've changed during the session is forgotten when RemSound closes; the file on disk is left exactly as it was. The lock setting is saved on the profile itself, so it sticks across launches. See Locking a profile for the full story.
**Change this profile's password …**| Alt+F, P| Shows the current password in plain text and lets you type a new one. See Passwords and encryption.
**Minimise to tray**|  Alt+F, N| Hides the window down to the system tray (the small icons near the clock). The tray icon's hover summary tells you what RemSound is doing, and right-clicking it gives you Show RemSound, Enable sending, Enable receiving, your Profiles submenu, and Exit — see The system tray icon and its menu for the full rundown. To bring the window back, double-click the tray icon, pick “Show RemSound” from its menu, or use the “Show or hide window” global hotkey: Ctrl+Shift+F10, unless you’ve changed it in Keyboard shortcuts.
**Exit**|  Alt+F, X (or Alt+F4)| Closes RemSound. If you have unsaved profile changes (and the profile isn't locked), it asks you first.

### Record menu

The recording feature can save what you're sending, what you're receiving, or both, to a file on your computer as a WAV, MP3, OGG-Opus or FLAC file. See Recording to a file for the full chapter; this is just the menu summary.

Item| Shortcut| What it does
---|---|---
**Start recording / Stop recording**|  Ctrl+R, or Alt+K, R| A toggle. The label switches between “Start recording” and “Stop recording” to show whichever action the next press would do. Either label is activated by the letter R. Each time you start, RemSound plays a short cue sound (if you've enabled it in Preferences), then creates a new recording in your recordings folder, named by date and time (see Recording to a file). Stopping closes the file and plays the stop cue. Ctrl+R works from anywhere in the main window.
**Open current recordings folder**|  Alt+K, O| Opens your recordings folder in Windows File Explorer. It creates the folder if it doesn't exist yet (which happens the first time on a fresh install).
**Change recordings folder …**| Alt+K, C| A folder picker. Choose a different folder for future recordings. The choice is saved in the current profile, so different profiles can record to different places.

### Service menu

The Service menu installs and controls the optional lock-screen service, which keeps sending your audio when you are not at the machine. See that chapter for the details. Items that can't be used right now are greyed out: for example, Start service is greyed out while the service is already running. Install, uninstall, start, stop and repair all need administrator permission, so Windows asks each time.

Item| Shortcut| What it does
---|---|---
**Service status** (first line)| —| Not something you pick: it says what the service is doing, for example “Service: installed, running”, with its version and how long it has been running.
**Configure service profile …**| Alt+J, C| Opens the service's own profile: who it sends to, its password, its server and what it sends. See The service profile, in detail.
**Install service**|  Alt+J, I| Sets the service up so it starts at every boot. RemSound asks you to confirm first.
**Uninstall service**|  Alt+J, U| Removes the service. RemSound asks you to confirm first.
**Start service**|  Alt+J, T| Starts the service now, without waiting for a reboot.
**Stop service**|  Alt+J, P| Stops the service now.
**Repair service folder access**|  Alt+J, R| Puts right the permissions on the service's settings folder, if saving the service profile or opening its logs ever fails. The folder belongs to the Windows account that set the service up; from any other account, RemSound warns that repairing it gives the service to this account instead, and asks first.
**View service log**|  Alt+J, V| Opens the service's own log. If there isn't one yet (service logging is off), it opens the service events log instead, which is always kept and records each install, start and stop, and any error.
**View service update log**|  Alt+J, L| Opens the record of the service updating itself. There is nothing in it until the service has updated at least once.

### DAW plugin menu

The DAW plugin menu installs and removes the plugin that puts RemSound inside your music software, and holds its two settings. See that chapter for the details.

Item| Shortcut| What it does
---|---|---
**Install plugin** (says **Reinstall plugin** once it's installed)| Alt+G, I| Asks where to put the plugin: in the standard place, which is your own plugin folder and needs no administrator permission, or in another folder you choose. RemSound remembers the folder and keeps the plugin up to date there.
**Remove plugin**|  Alt+G, R| Takes the plugin away again, from whichever folder it is in. Greyed out when the plugin isn't installed.
**Apply pan and EQ to plugin audio** (tickable)| Alt+G, Q| On by default: the volume, pan and EQ you've set for each peer come through to your music software. Untick it to get their untouched sound instead.
**Let plugins connect to RemSound** (tickable)| Alt+G, L| On by default. Untick it and RemSound stops listening for plugins altogether.

### Options menu

The Options menu gathers everything you might want to configure about the app — recording settings, keyboard shortcuts, profile passwords, and general preferences.

Item| Shortcut| What it does
---|---|---
**Recording settings …**| Alt+O, S| Opens the Recording settings dialog. Up to five lists: _Recording source_ (Alt+S), _File format_ (Alt+F), _Audio format attributes_ (Alt+A), _FLAC compression level_ (Alt+L — only shown when FLAC is chosen), and _Channels_ (Alt+C) — plus two tickboxes: _Split recording into separate tracks_ (Alt+T, one file per peer) and _Bypass pan and EQ when recording_ (Alt+R, record the raw audio). The attributes list changes to match the format you pick. OK saves to the current profile; Cancel discards.
**Keyboard shortcuts …**| Ctrl+K, or Alt+O, K| Opens the global hotkey dialog (turning sending and receiving on or off, volume, show or hide window, start/stop recording, remote-control commands, speak the status line).
**Profile passwords …**| Alt+O, W| Lists every profile alongside its password, so you can view or change any of them in one place.
**Manage named peers …**| Alt+O, N| Opens a list of every peer you've given a friendly name to, showing each one's machine name and where and when you last connected to it. Pick one in the **Peers** list (Alt+P) and press **Rename (Alt+R** , or F2) to change its name, or **Delete (Alt+D** , or the Del key) to forget it. **Close** (Alt+C) closes the list. Deleting only drops the name: the peer still connects as normal under its machine name. See the peer-naming notes on the Connectivity tab.
**Enable Realtek ASIO driver in RemSound** (or **Disable Realtek ASIO driver in RemSound**)| Alt+O, E (or Alt+O, D)| Only shown if a Realtek ASIO driver is installed. Lets you reverse the choice RemSound offered about disabling that driver (Realtek's generic ASIO driver tends to grab the wrong device and clash with your screen reader).
**Install RemSound on this PC …** (or **Uninstall …**)| Alt+O, I (Alt+O, U once installed)| Turns the copy you're running into a properly installed Windows app — on the Start menu, with a desktop shortcut, and listed in Windows’ Installed apps — or removes it again. Once it's installed, this item changes to **Uninstall RemSound from this PC**. See Installing RemSound on your PC.
**Preferences …**| Ctrl+P, or Alt+O, P| Opens Preferences. Seven tabs: General, Connectivity, Appearance, Audio cues, Startup behaviour, Update settings and Logging. Ctrl+Tab moves between them, or the arrow keys once the tab names have focus. Esc closes it. See what is on each tab.

### Installing RemSound on your PC

RemSound normally runs “portable” — you unzip it and run it straight from whatever folder you unzipped it to, and it keeps its settings, profiles and recordings inside that same folder. That works, and you never have to install anything. But if you'd rather have RemSound set up as a proper Windows app — on the Start menu, with a desktop shortcut, and listed in Windows’ _Installed apps_ — open **Options → Install RemSound on this PC** (Alt+O, I).

It installs into your own user area (`…\AppData\Local\Programs\RemSound`), so it **never asks for administrator rights** and only affects your account. A dialog lets you choose what to set up. Tab through the tick-boxes, then select **Install** (Alt+I), or **Cancel** (Alt+C) to leave things as they are. Pressing Enter on a tick-box won't skip ahead — you reach the Install button by tabbing to it:

  * **Create a desktop shortcut** (Alt+D) — puts a RemSound shortcut on your desktop that starts the installed copy. On by default. Left unticked, a RemSound shortcut already on the desktop is removed.
  * **Add to the Start menu** (Alt+S) — makes a “RemSound” folder on the Start menu holding three shortcuts: the program, the RemSound manual, and an uninstall shortcut. On by default. Left unticked, a RemSound Start menu folder that is already there is removed.
  * **Run RemSound when I sign in to Windows** (Alt+R) — starts the installed copy each time you sign in to Windows, the same setting as on the Startup behaviour tab. Off unless you already had it turned on; left unticked, RemSound no longer starts when you sign in.
  * **Copy my profiles and settings across** (Alt+P) — brings your profiles and every setting (keyboard shortcuts, named peers, colour theme, and the rest) from the copy you're running into the install folder. On by default.
  * **Copy my recordings across** (Alt+E) — copies the recordings folder from the copy you're running into the install folder. On by default. Recordings in a folder you chose yourself stay where they are.
  * **Copy my logs across** (Alt+L) — copies your diagnostic logs from the copy you're running into the install folder. Off by default; you rarely need old logs in the installed copy.



If RemSound is already installed there, the same dialog is called **Update the RemSound install** and its button is **Update** (Alt+U): it updates that installed copy instead.

Control| Shortcut| What it does
---|---|---
**Install** (or **Update**)| Alt+I (Alt+U for Update)| Installs RemSound into your own user area with the choices you ticked; no administrator rights are needed. When it's done, press OK on the message: RemSound offers to set up the lock-screen service if it isn't installed yet, then closes and reopens from the installed copy. When RemSound is already installed, this button is **Update** and updates that copy instead. Enter on a tick box doesn't press it; tab to it.
**Cancel**|  Alt+C, or Escape| Closes this dialog without installing anything. The copy you're running carries on exactly as it was.

When you select Install, RemSound first offers to save any unsaved changes to your profile (Cancel stops the install) and finishes a recording if one is running, so what is copied is up to date. Then it copies itself into place and **closes and reopens from the installed location**. A confirmation dialog tells you this is about to happen before it does. After installing, RemSound also offers to set up the lock-screen service, and then to start it; you can say no and do it later from the Service menu. From then on your settings, profiles and recordings live inside the installed folder, exactly as they did in the portable copy, and RemSound still updates itself in place as usual.

**Uninstalling.** Once RemSound is installed, that same menu item becomes **Uninstall RemSound from this PC** (Alt+O, U). You can also uninstall from the “Uninstall RemSound” shortcut in its Start-menu folder, or from Windows’ _Installed apps_ list — all three do exactly the same thing. RemSound asks you to confirm first. **Remove profiles, config and logs** (Alt+P) and **Remove recordings** (Alt+R) are both **off by default** , so unless you tick them your own files are kept even after the program itself is removed. If the DAW plugin or the lock-screen service is installed, there is also a tick box to remove each one: **Remove the DAW plugin too** (Alt+D) and **Remove the RemSound service too** (Alt+S; Windows asks for administrator permission). Those two start ticked: left behind, the plugin would have nothing to connect to, and the service would never be updated again. Then choose **OK** (Alt+O) or **Cancel** (Alt+C). From the Options menu, RemSound then offers to save any unsaved changes (Cancel stops the uninstall) and finishes a recording if one is running. A short message confirms once it's done, and RemSound closes the normal way.

Control| Shortcut| What it does
---|---|---
**Remove profiles, config and logs**|  Alt+P| Deletes your profiles, your settings and your logs (the **user settings and logs** folder inside the install folder) along with the program. Off by default, so they are kept, and the install folder is left holding them.
**Remove recordings**|  Alt+R| Deletes the **recordings** folder inside the install folder along with the program. Off by default, so your recordings are kept. Recordings saved to a folder you chose outside the install folder are never touched.
**Remove the DAW plugin too**|  Alt+D| Only there when the DAW plugin is installed. Ticked by default, because the plugin has nothing to connect to once RemSound is gone. It removes the plugin files RemSound put there, from whichever folder the plugin is in; if that folder needs administrator permission, Windows asks for it. If the files can't be removed, the closing message says so, and you can remove them yourself.
**Remove the RemSound service too**|  Alt+S| Only there when the lock-screen service is installed. Ticked by default, because a service left behind would keep sending and would never be updated again. Windows asks for administrator permission to remove it; if that is declined, the service stays installed and the closing message says so.
**OK**|  Alt+O| Starts the uninstall. RemSound removes the DAW plugin and the service if they are ticked, then its shortcuts and its entry in Windows’ Installed apps, and tells you when it's done. Press OK and it closes, and its folder is deleted, apart from any data you chose to keep. Enter on a tick box doesn't press OK; tab to it.
**Cancel**|  Alt+C, or Escape| Closes this dialog without removing anything. RemSound stays installed exactly as it was.

### Help menu

The Help menu opens this manual, checks for updates, and shows the About dialog.

Item| Shortcut| What it does
---|---|---
**Help**|  Shift+F1 (anywhere), or Alt+H, H| Opens this whole user manual in your default web browser. Shift+F1 also works inside every dialog and in the startup profile picker, before the main window has even loaded. For help on just the control you're on, press F1 instead — see Context help.
**Check for updates**|  Alt+H, C| Checks RemSound's releases page on GitHub for a newer version. If there is one, you get a confirmation dialog with the release notes and a Yes / No to install. If you're already up to date, a popup tells you so. (To have RemSound check on its own instead of pressing this button, see Updating RemSound.)
**About RemSound**|  Alt+H, A| A small dialog showing the version you're running and the latest release notes in a scrollable read-only box. Close (or Esc) dismisses it.

### The Appearance tab (Preferences)

The **Appearance** tab of Preferences (Options → Preferences, or Ctrl+P) controls how the window looks and is laid out. Nothing here affects the sound or the screen reader. Changes take effect as soon as you close Preferences — except the colour theme, which applies the next time you start RemSound.

Control| Shortcut| What it does
---|---|---
**Colour theme**|  Alt+T| A drop-down list: _Match Windows_ (the default), _Light_ , or _Dark_. RemSound follows your Windows light or dark setting unless you pick a fixed one. It changes only how the window looks, makes no difference to a screen reader, and takes effect the next time you start RemSound.
**Show the volume, pan and EQ for peers tab**|  Alt+Q| On by default. Untick to hide the Volume, pan and EQ for peers tab from the main window. The change is kept on this computer, applies to every profile, and shows on the main window when you close Preferences.
**Tab order**|  Alt+O| A list of the main window's tabs. Pick one, then use **Move up (Alt+U)** and **Move down (Alt+N)** to change the order the tabs appear in — which also sets their Ctrl+number (Ctrl+1 is always whichever tab is first). All four tabs are listed even when the volume/pan/EQ tab is hidden. The new order is saved straight away and shows on the main window when you close Preferences.
**Move up**|  Alt+U| Moves the tab highlighted in the Tab order list one place up, so it comes earlier in the main window’s row of tabs and takes the lower Ctrl+number. Nothing happens if it is already first. The new order is saved straight away and shows on the main window when you close Preferences.
**Move down**|  Alt+N| Moves the tab highlighted in the Tab order list one place down, so it comes later in the main window’s row of tabs and takes the higher Ctrl+number. Nothing happens if it is already last. The new order is saved straight away and shows on the main window when you close Preferences.
**Enable the discovered peers list on the Connectivity tab**|  Alt+D| On by default. Untick to hide the Discovered peers not on a server list from the Connectivity tab of the main window. Hiding it changes nothing about how you connect. The change applies to every profile and shows when you close Preferences.
**Enable the remembered peers list on the Connectivity tab**|  Alt+R| On by default. Untick to hide the Remembered peers list from the Connectivity tab of the main window. Hiding it changes nothing about how you connect, and the peers in it are still remembered. The change applies to every profile and shows when you close Preferences.

## 6. Connectivity tab (main window)

The Connectivity tab is where you manage peers: the people you're connected to, the people RemSound has found on your network or on a server, the people you've connected to before, and connecting to a server or to a peer by its address. (The logging options are on the **Logging** tab of Preferences — see Logs and diagnostics.)

The controls on this tab, in tab order:

Control| Shortcut| What it does
---|---|---
**Connected peers**|  Alt+C| The people you currently have sound flowing with. Unticking a row, or pressing **Delete** on it, disconnects that peer.
**Peer details**|  Alt+E| A read-only box describing whichever connected peer you're on in the list above. Arrow through it to read: their name, their machine name, their IP address, how long you've been connected, the link health and round-trip time, what they're sending (how many devices, on WASAPI or ASIO, at what sample rate and codec), and whether they're receiving your audio. The device and WASAPI/ASIO detail only shows while you're actually receiving that peer.
**Rename peer**|  Alt+M or F2| Give the highlighted peer a friendly name of your choosing. It opens a box with the name and a **Clear custom name** button. The name sticks to that machine for good — across restarts, IP changes and networks — and shows everywhere that peer appears: the lists here, the volume, pan and EQ for peers tab, the status line and split-recording filenames. See below.
**Discovered peers not on a server**|  Alt+D| People on your own network that RemSound has heard from in the last few seconds. Tick someone to connect to them.
**Discovered peers on server**|  Alt+0| Only there while you're on a server: the people on it who have the same password as you and whom you haven't connected to. Tick someone to connect to them, exactly as in the other lists, and they move to **Connected peers**. They have to tick you too before any sound flows — until they do, the line beside the server button says you're waiting for them. See Servers.
**Remembered peers**|  Alt+R| People you've connected to before, or added by address. This list is kept between sessions. Somebody whose name another device on your network also has, such as two phones both called iPhone, is remembered by their address instead of their name, so ticking them finds that device, not the other one. Tick someone to reconnect. Press **Delete** on an entry to forget it.
**Connect to server**|  Alt+N| Opens the server window: an address box, the servers you've used before, a Connect button and Close. While you're on a server this button says **Disconnect from or change server** , and the button inside says Disconnect. See Servers.
**Add peer by IP**|  Alt+A| Opens a small box where you type an address or computer name. It adds that peer to the remembered list and connects. If the address turns out to be a server, RemSound offers to connect to it as one instead.
**Lock to these exact peer addresses**|  Alt+L| When ticked, this profile uses only the exact addresses you set and never follows the other computer by name or switches to a different address — even if the address stops working. Off by default, saved with the profile. See Locking a profile to one exact address.
**Connection status**|  Alt+S| A read-only box of text that sums up everything happening right now — how long you've been connected, how many peers you have, how much sound is flowing each way, and the connection health of each peer. Open it to read the current connection status.

The **Discovered** and **Remembered** peer lists can each be hidden if you don't use them — untick them on the Appearance tab of Preferences. Hiding one just removes it from this tab; it changes nothing about how you connect.

### What's on each Preferences tab

Tab| What's on it
---|---
**General**|  The profiles folder (**Browse for RemSound profiles folder** , Alt+B). **Auto-save non-read-only profiles (Alt+A)** : Never (the default), or every 2, 5, 10, 15, 20 or 30 minutes. A locked profile is never auto-saved. **Accept remote volume commands from peers (Alt+V)** , saved with the profile; see Remote control. And **Clear remembered applications list (Alt+L)** , which asks you to confirm, then empties that list for every profile.
**Connectivity**| **Accept connections from other peers (Alt+N)** , described below. **Automatically open my router for incoming connections (UPnP) (Alt+O)** , with a status line under it; see Automatic router port opening. And **Clear remembered peers list (Alt+P)** and **Clear remembered servers list (Alt+S)**. Each clear button asks you to confirm, then empties that list for every profile.
**Appearance**| **Colour theme (Alt+T)** — Match Windows, Light or Dark. It changes how the window looks, takes effect next launch, and makes no difference to a screen reader. “Show the volume, pan and EQ for peers tab”, on by default. **Tab order (Alt+O)** — the window's tabs, with **Move up (Alt+U)** and **Move down (Alt+N)**. And two ticks to show or hide the **Discovered (Alt+D)** and **Remembered (Alt+R)** peer lists on the Connectivity tab.
**Audio cues**|  The cue list and the sound each one plays. See Audio cue sounds.
**Startup behaviour**|  Start minimised, start with Windows, start with a particular profile, and the context help message.
**Update settings**|  How often RemSound checks for a new version, and what it does when it finds one.
**Logging**|  Turn logs on, write them now, and tidy the log folder. See Logs and diagnostics.

#### General tab

Control| Shortcut| What it does
---|---|---
**Browse for RemSound profiles folder**|  Alt+B| Opens a folder picker, starting in the folder your profiles are in now, so you can choose a different folder for them. A message confirms the new folder, which is saved on this computer and used from the next time RemSound starts. Profiles already in the old folder are not copied across.
**Auto-save non-read-only profiles**|  Alt+A| A list: _Never_ (the default), or every 2, 5, 10, 15, 20 or 30 minutes. At that interval RemSound quietly saves the profile you have open, with no sound and no message, but only if it has unsaved changes. A locked (read-only) profile, or a new profile that has never been saved, is never auto-saved. The setting is kept on this computer and applies to every profile.
**Accept remote volume commands from peers**|  Alt+V| When ticked, a peer you have ticked, using the same password, can use their remote-control hotkeys to turn your RemSound volume or your Windows volume up or down, or mute it. Off by default; while it is off, those commands are ignored. It is saved with the profile, so save the profile (Ctrl+S) to keep the change. See Remote control.
**Clear remembered applications list**|  Alt+L| Empties the list of applications RemSound has remembered for sending, for every profile. It asks you to confirm first, Yes or No. Applications you are sending now carry on, and an application is remembered again the next time you tick it.

#### Connectivity tab

Control| Shortcut| What it does
---|---|---
**Accept connections from other peers**|  Alt+N| A list of what happens when somebody who has your profile's password ticks you before you have ticked them. _Ask me each time_ (the default) asks you first; _Automatically_ ticks them back straight away so sound flows; _Manual_ does nothing until you tick them yourself. Nobody without your password is ever asked about or accepted. A phone that only listens has to be ticked by you the first time, until its app is updated; after that it's let back in by itself. The choice is saved straight away, kept on this computer, and applies to every profile. See When somebody else ticks you.
**Automatically open my router for incoming connections (UPnP)**|  Alt+O| When ticked, RemSound asks your router to open a port so peers on the internet can reach this computer, without you setting up port forwarding by hand. A status line under the box says whether the router agreed. Unticking it asks the router to close the port again. Off by default, and kept on this computer; see Automatic router port opening.
**Clear remembered peers list**|  Alt+P| Empties the list of peers RemSound has remembered, for every profile. It asks you to confirm first, Yes or No. Peers you are connected to now are not affected, and any peer is remembered again the next time you connect to it. It also forgets the devices you've ticked, so a phone that only listens has to be ticked again before it's let back in by itself.
**Clear remembered servers list**|  Alt+S| Empties the list of servers RemSound has remembered, for every profile. It asks you to confirm first, Yes or No. A server you are connected to now is not affected, and any server is remembered again the next time you connect to it.

#### Closing Preferences

Control| Shortcut| What it does
---|---|---
**Close**|  Alt+C, Enter or Escape| Closes Preferences and takes you back to the main window. Nothing is thrown away: every setting was saved the moment you changed it, and changes to the main window’s tabs and peer lists show as soon as Preferences closes. If you changed a setting that is saved with the profile, such as Accept remote volume commands or one of the profile’s cue sounds, the profile is marked as having unsaved changes, so save it (Ctrl+S) to keep them.

### When somebody else ticks you

RemSound has always needed both of you to tick each other before any sound flows. That is still true, but you can choose what happens at your end when somebody ticks you. On the Connectivity tab of Preferences, **Accept connections from other peers (Alt+N)** gives three choices:

Choice| What happens
---|---
**Ask me each time**|  The default. A box appears saying who it is and where they are, and nothing happens until you answer. You are asked once per person each time RemSound runs, whichever way you answer.
**Automatically**|  They are ticked back the moment they tick you, so sound flows without either of you doing anything else.
**Manual**|  Nothing happens until you tick them yourself. How RemSound worked before this setting existed.

The send-only Windows service has its own version of this, in **Additional service options** when you set the service up: **Accept people who tick this service on a server**. Only two choices there, because there is nobody at a screen to ask — off by default, which means the service reaches exactly the people its profile named when you saved it. Tick it and somebody who joins the server later can reach the service without you opening the app again. That includes a phone or older app the server pairs with the service: it gets sound only when this is ticked.

**Only people with your password.** Anybody who has your profile's password is asked about or accepted, whether they send you audio, only listen, both or neither — and nobody without it, ever. Three things show that somebody has ticked you and has your password: a proof RemSound sends every few seconds to everybody it has ticked directly, sealed with the password so it can't be faked or copied; their audio arriving when you haven't ticked them, which carries the password's fingerprint; and the server's list, which only ever shows people on your password. So a phone that ticks your computer only to listen is accepted from its proof, once its app sends one. Until the iPhone and Android apps are updated to send it, a phone that only listens has to be ticked by you the first time. After that RemSound remembers that device — the device itself, not its name, so another phone called iPhone is never mistaken for it — and lets it back in whenever it connects to listen, following this same setting. Clearing the remembered peers list in Preferences forgets it. The setting is kept on this computer, so it holds whichever profile you load.

**Unticking somebody keeps them unticked.** If you untick or delete someone, RemSound remembers that you did, and won't tick them back or ask about them again for the rest of the session — even on Automatic, and even though their audio keeps arriving because they still have you ticked. Tick them again yourself and it starts over. Without this you could never let anyone go: they came back the moment you dropped them.

### Giving a peer a friendly name

Highlight a peer in the **Connected peers** list and press **Rename peer (Alt+M)** to call them whatever you like — “Andre's desktop” instead of “ANDRE-DESKTOP”. The name is tied to that _machine_ , not to its address, so it survives their restarting RemSound, their address changing, and their reaching you on a different network (local network one day, Tailscale the next). Once set, it replaces the machine name everywhere that peer shows up — both peer lists, the volume, pan and EQ for peers list, the connection status, and the per-peer files a split recording makes.

In the rename box, type the name and press OK, or press **Clear custom name (Alt+C)** to drop back to the machine name. (Leaving the box empty and pressing OK does the same.) **Cancel** (Alt+A) or Escape closes the box without changing anything. The names are kept per machine you're using RemSound on, and apply in every profile. One case to know about: a peer you added purely by address that never announces a name has no machine name to pin to, so its friendly name is tied to the address instead and would need re-setting if that address changes.

#### The Rename peer box

Control| Shortcut| What it does
---|---|---
**Friendly name**|  Alt+N| The name you want this peer shown by, instead of its machine name, which the window also shows. It starts with the peer's current friendly name, if it has one. Leave it empty and press OK to go back to the machine name.
**Clear custom name**|  Alt+C| Removes this peer's friendly name straight away and closes the box, so the peer is shown by its machine name again. This applies in every profile on this computer.
**OK**|  Alt+O, or Enter| Keeps the name in the box and closes it. The name is saved at once on this computer, applies in every profile, and shows everywhere this peer appears. An empty box removes the friendly name.
**Cancel**|  Alt+A, or Escape| Closes the box without changing the peer's name.

To see and tidy up all your named peers in one place — including ones that are currently offline — use **Options → Manage named peers**. It lists each one with its machine name and when you last connected, and lets you rename or delete any of them.

#### The Manage named peers window

Control| Shortcut| What it does
---|---|---
**Peers**|  Alt+P| Every peer you've given a friendly name to, including any that aren't online now, in order of name. Each is read as its friendly name, its machine name, and when and at which address it was last seen. Press F2 to rename the highlighted peer, or Delete to forget its friendly name.
**Rename**|  Alt+R| Opens the Rename peer box for the peer highlighted in the list, to change its friendly name or clear it. The new name is saved at once and applies in every profile on this computer.
**Delete**|  Alt+D| Forgets the highlighted peer's friendly name straight away, without asking. The peer itself is not affected: it still connects as normal, shown by its machine name.
**Close**|  Alt+C, Enter or Escape| Closes the Manage named peers window. Renames and deletions you made in it are already saved, so there is nothing to undo.

## 7. Audio inputs and outputs tab

The Audio inputs and outputs tab controls everything to do with which sound devices are involved. The ASIO driver picker near the top decides whether ASIO is being used at all. The Receive side and the Send side each have their own master checkbox and their own device lists.

Control| Shortcut| What it does
---|---|---
**Uncheck all inputs and outputs on all soundcards and set ASIO driver to none**|  Alt+U| A button, and the first thing on the tab. It unticks every device in every list and sets the ASIO driver to _(none)_ , all in one go. It doesn't ask first. It leaves Receive audio and Send my audio as they were.
**ASIO driver**|  Alt+D| A list that starts with _(none)_. Pick _(none)_ and the app uses the ordinary Windows sound path only; pick a real driver and the ASIO device lists appear below, and the Audio profile tab gains a second delay setting. If your computer has no ASIO drivers installed, this control is hidden completely. If the driver can't be opened when RemSound needs it, say because the interface isn't back yet after sleep or a music program is holding it, RemSound tries it again every ten seconds until it opens.
**Receive audio**|  Alt+R| The master switch for receiving. When it's off, no sound plays out, no matter which output devices are ticked, and RemSound doesn't open those devices at all, your ASIO interface included, so a music program can use them. Tick it and they open. A DAW plugin set to receive says so in its status while this is off.
**WASAPI outputs for received audio**|  Alt+3| Tick which ordinary Windows outputs (speakers, headsets) should play the received sound. Ticking more than one means the received sound plays out of all of them at once.
**ASIO outputs for received audio**|  Alt+1| (Shown when an ASIO driver is chosen.) Tick which ASIO channel pairs should play the received sound.
**Master volume for received audio**|  Alt+V| A slider: the master volume for everything coming in. There is no separate volume per device here; per-person volume lives on the Volume, pan and EQ for peers tab.
**Send my audio**|  Alt+S| The master switch for sending your own audio. When it's off, none of your devices or applications is sent, whichever are ticked. A music track sent by the DAW plugin has its own switch in the plugin, and goes on sending whatever this one says.
**WASAPI audio outputs to send**|  Alt+4| Tick which Windows output devices to capture from — this captures whatever is currently playing on those speakers and sends it.
**WASAPI audio inputs to send**|  Alt+5| Tick which Windows input devices to capture (microphones, line-ins).
**ASIO audio inputs to send**|  Alt+2| (Shown when an ASIO driver is chosen.) Tick which ASIO channel pairs to capture and send.

All the device lists are checkable lists — tick or untick an item to include or exclude that device. Profiles save which devices are ticked; a new profile starts with everything unticked.

### Following the Windows default audio device

At the very top of the **WASAPI outputs for received audio** list, the **WASAPI audio outputs to send** list and the **WASAPI audio inputs to send** list there's a special entry: **Use Windows default audio device, follows Windows changes**. Tick it and RemSound uses whatever Windows currently treats as the default — the default speakers for received sound, the default output for what you send, the default microphone for input — and, the useful part, it **follows that default on its own**. Make a headset your default and RemSound switches to it; unplug it and RemSound moves back, all without you touching the list.

Ticking it is **exclusive** : RemSound unticks every specific card in that list and locks them so they can't be re-ticked while the default entry is on. That keeps things unambiguous — in each list you're either following the Windows default or picking specific cards, never a mix. Untick the default entry and the specific cards become available again.

The specific cards you tick are saved in your profile. The “Use Windows default” choice is kept on this computer instead, so it applies whichever profile you load. It's safe to keep because it can never point at the wrong card — it always means whatever Windows is using right now.

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

By default the WASAPI output list sends _everything_ playing on a sound device. If you'd rather send only **one particular program** — say foobar2000 or a browser — and nothing else, use the **How to send WASAPI audio (Alt+6)** chooser, just below _Send my audio_. It has two settings. _Send whole audio devices_ , the default, sends the devices ticked in the WASAPI output list. _Send specific applications_ swaps that list for two lists of programs, and needs Windows 10 version 2004 or newer. In more detail:

  * **Send whole audio devices** (the default) — the ordinary behaviour, using the “WASAPI audio outputs to send” list described above.
  * **Send specific applications** — swaps that device list for two application lists. (This needs Windows 10 version 2004 or newer; on older Windows the chooser is hidden and only whole-device sending is available.)



When you choose _Send specific applications_ , two lists appear:

List| What it holds
---|---
**Currently active applications (Alt+8)**|  Every program making sound right now. Tick one and only that program's audio is captured and sent — its own private stream, separate from everything else on the machine. Tick several to send several. A program you've ticked that isn't running at the moment still shows here marked _(not running)_ , so you can always find it and untick it; it starts being sent again the instant it reopens.
**Remembered applications (Alt+9)**|  Your saved “apps I send” address book — shared across all your profiles, like the remembered peers list. A program joins this book the moment you first _tick_ it in either list — that's the only way in, so after clearing the book it refills as you tick apps again. Tick a program here and it moves up to the active list the moment it's running (and is captured from its very first sound). Untick a program in either list and it drops back here. Press **Delete** on an entry to forget it, just like the remembered peers list.

Because sending is by program _name_ , your choice survives that program being closed and reopened, or even the computer restarting. There is deliberately no “send everything” option in applications mode — if you want the whole machine's sound, that's what _Send whole audio devices_ is for.

## 8. Audio profile tab

The Audio profile tab holds everything that shapes the trade-off between sound quality and delay. The first control on the tab is the _priority mode_ checkbox — it sits on its own at the top because it has the biggest single effect on how the audio feels in the first few seconds. Below it are two groups: **Audio send parameters** first, then **Audio receive parameters**.

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
 **Audio codec**|  Alt+C| A list. The codec is the method RemSound uses to package the sound before sending it. Three choices: PCM 48k 24-bit (uncompressed), Opus broadcast quality (loss tolerant), or Opus live latency (for jamming and monitoring). For which to choose, see Latency and audio quality.
 **Packet size**|  Alt+P| A list: _Standard_ (the default) or _Small_ (for a local network only). With PCM or Opus broadcast quality, Small halves each packet: it saves a little delay on the sending side, but doubles how many packets are sent. Opus live latency already uses the smallest packet there is, so Small makes no difference to it. See Latency and audio quality.

 RemSound always **times its sending from the sound device's own hardware clock**. There is nothing to set; see Locking to the audio clock.

 ### Audio receive parameters

 What you see in this section depends on whether an ASIO driver is chosen on the Audio inputs and outputs tab. With no ASIO driver, you see one delay setting (labelled “Audio jitter buffer in milliseconds”). With an ASIO driver chosen, you see two delay settings — one for each sound path — each with its own auto-tune toggle. The two paths are independent: a problem on one doesn't affect the other.

 Control| Shortcut| What it does
 ---|---|---
 **ASIO jitter buffer in milliseconds**|  Alt+I| (Only when an ASIO driver is chosen.) A small up/down number control. It sets the target amount of sound to keep buffered for the ASIO path. It starts at 10 ms, which suits two computers on the same wired network or the same machine. Over anything else, going below the network’s real-world jitter (typically 15–25 ms) causes constant tiny corrections that you can hear, so start at 25 ms and come down from there — or turn on auto-tune and let it find the level.
 **Continuous auto-tune ASIO jitter buffer**|  Alt+T| (Only when an ASIO driver is chosen.) A checkbox. When it's on, RemSound nudges the ASIO jitter buffer automatically as the ASIO path's jitter changes. It works independently of the WASAPI path's auto-tune. How often it re-checks is set by the **Auto-tune interval** list.
 **WASAPI jitter buffer in milliseconds** (called “Audio jitter buffer in milliseconds” when there's no ASIO driver)| Alt+W (Alt+L when no ASIO driver)| A small up/down number control. It sets the target amount of sound to keep buffered for the WASAPI path (or the only path, in WASAPI-only setups). Smaller means less delay but more clicks. It starts at 80 ms. Most people want 20–80 ms.
 **Continuous auto-tune WASAPI jitter buffer** (called “Continuous auto-tune jitter buffer” when there's no ASIO driver)| Alt+Y (Alt+T when no ASIO driver)| A checkbox. When it's on, RemSound nudges the WASAPI jitter buffer automatically as the network changes. How often it re-checks is set by the **Auto-tune interval** list beside it.
 **Auto-tune interval**|  Alt+N| A list: how often continuous auto-tune re-checks the jitter buffer — 3, 5 (the default), 10, 15, or 30 seconds. Its label follows what you are actually using: “Auto-tune interval for jitter buffer” when only one kind of output is ticked (WASAPI only, or ASIO only), and “Auto-tune interval for WASAPI and ASIO jitter buffer” when you have both ticked, because this one timer drives both paths' auto-tuning. Each path still settles at whatever jitter buffer its own calculation chooses; only the timing of the re-checks is shared.
 **Total latency**|  Alt+M| A read-only box. It shows three figures: the **jitter buffer** you have set; what the **sound card and hardware add** on top of it (capturing the sound, packing it up, the network, and your sound card's own output buffer, measured as it runs); and the **total latency** , the two added together, one way from their microphone to your ears. The total says “approximately”, because most of it is worked out rather than timed. While no sound is arriving it says “not receiving”. When you receive through both WASAPI and ASIO, it has one line for each. See Total latency: what the box is telling you.
 **Buffer smoothness**|  Alt+B| A list, 1 to 10. It controls how patient the receiving side is with sound that arrives late, on either path. Higher means more protection from clicks but a longer steady delay. Default 3.
 **Artefact sound type**|  Alt+A| A list: how a momentary gap in the sound is filled. _Noise burst_ (the default) fills a momentary gap with a brief soft hiss, which blends into music. _Click_ leaves the gap unfilled so you hear an obvious click — useful when you want to hear every problem.

 Most people only need to pick a codec and a smoothness level, and leave everything else at its default.

 ## 9\. Volume, pan and EQ for peers tab

 The Volume, pan and EQ for peers tab lets you shape the sound of each peer you're connected to. You can set how loud that person is, lean them to the left or right, and change their tone with an equaliser. It's handy when you have several people connected at once and want to mix them — for a jam session you might put the drummer over to the left, turn someone down a little, or brighten someone up.

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

   * Tab past the mode picker to the **Add band** button and press it. A small dialog opens with three boxes: a **start frequency** (Alt+S), an **end frequency** (Alt+E) and a **gain in dB** (Alt+G, from −12 to +12). Each box you can type into or spin with the arrow keys; they only accept sensible numbers. As you change the values you hear the band on that peer straight away. Press **OK** (Alt+O) to add it, or **Cancel** (Alt+C) or **Escape** to drop it.
   * The **Bands** list holds your bands, one per row, each read out as its range and level — for example “200 Hz to 800 Hz, plus 3 dB”. The list is sorted low to high, so the bass bands are at the top and the treble at the bottom. Up and down arrow move between bands; **left and right arrow nudge the selected band's gain down or up by half a dB** , so you can fine-tune it on the fly and hear the change straight away.
   * To remove a band, land on it and press **Delete** , or use the **Delete band** button. You can select several at once (hold Shift and arrow, or hold Ctrl and arrow then Space to pick out individual ones) and delete them together.



 #### The Add EQ band dialog

 Control| Shortcut| What it does
 ---|---|---
 **Start frequency in Hz**|  Alt+S| Where the new band begins, from 20 to 20,000 Hz. It starts empty: type a number, or arrow up and down in steps of 10. Once both frequencies are set, you hear the band on that peer as you change them.
 **End frequency in Hz**|  Alt+E| Where the new band ends, from 20 to 20,000 Hz; it must be higher than the start frequency. It starts empty: type a number, or arrow up and down in steps of 10. Once both frequencies are set, you hear the band on that peer as you change them.
 **Gain in dB**|  Alt+G| How much the band boosts or cuts, from −12 to +12 dB. It starts at +3 dB. Type a number, or arrow up and down in half-dB steps; once both frequencies are set, you hear the change on that peer straight away.
 **OK**|  Alt+O, or Enter| Adds the band to that peer's 16 band parametric EQ and closes the dialog. If a frequency is missing or out of range, or the end isn't higher than the start, RemSound says so and you stay in the dialog. The band is saved with the profile, so the profile now has unsaved changes.
 **Cancel**|  Alt+C, or Escape| Closes the dialog without adding the band, and that peer's sound goes back to how it was before.

 Each parametric band is a boost or cut spread across the range between its start and end frequencies. A wide range affects a broad sweep of the sound; a narrow one is more surgical.

 The three modes are kept separate. Switching between them keeps each one's own settings — nothing carries across from one to another. Only the mode you've picked is the one you hear.

 Everything here updates in **real time** — you hear the change as you move a control — and it adds no extra delay to the audio. All of it (the master switch's setting is saved, and each peer's tick, volume, pan and EQ) is stored with the profile. The one exception is the global “toggle everything” keyboard shortcut, which is machine-wide rather than per-profile. There's also a small EQ response graph on the tab; it's purely a visual picture of the shape you've dialled in and plays no part in how you use the tab with a screen reader.

 ## 10\. ASIO and WASAPI

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
   * **Realtek ASIO** — bundled with some Realtek drivers. _Best avoided_ ; see below.



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

 RemSound has no buffer-size control of its own. To change the ASIO buffer size, open the control panel program that came with your audio interface (such as NI's Komplete Audio Control Panel or the Audient EVO software) and set it there. The driver remembers its buffer size between sessions; RemSound uses whatever the driver is set to.

> **About Realtek ASIO:** if you see “Realtek ASIO” in the driver list, be careful with it. Despite the name, it isn't tied to Realtek hardware — it's a generic driver that opens whatever Windows treats as the default sound device. On a computer that has a real audio interface (Audient, Komplete, and so on), choosing Realtek ASIO will often grab _that_ interface and end up fighting both your real ASIO driver and your screen reader for the same hardware. It's usually best to ignore Realtek ASIO completely.

> **What RemSound does about it:** if a Realtek ASIO driver is installed, RemSound spots it on startup and offers, just once, to disable it — partly for the device-grabbing reason above, and partly because it leaks Windows resources every time it's opened. Say yes and RemSound adds it to a never-touch list and takes it out of the driver picker, so it can't be chosen by accident. You can reverse that — or disable it later if you kept it — any time from **Options → Enable / Disable Realtek ASIO driver in RemSound**. Once you've answered the startup question, RemSound won't ask again.

 ### Same driver, sending and receiving, on one computer

 RemSound supports this — you can capture from your audio interface and play received sound out of the same interface at the same time, on the same computer. Most modern professional audio drivers handle this fine.

 ## 11\. Peers — finding and connecting

 A “peer” is another computer running RemSound that you want to talk to. You manage peers on the **Connectivity** tab. It has three lists, all of them checkable, and a fourth while you’re on a server:

 List| Contents| What ticking does
 ---|---|---
 **Connected peers**|  People you currently have sound flowing with.| Unticking disconnects. So does pressing Delete on a row.
 **Discovered peers not on a server**|  People RemSound has heard from in the last few seconds — either from an announcement sent across your local network, or from a direct announcement (which is how it works over Tailscale and other VPNs).| Connects you to that peer. Sound starts flowing both ways.
 **Remembered peers**|  People you've connected to before, plus any addresses you've typed in by hand. This list is kept between sessions.| Connects to that remembered peer if they're online (and adds them as a manual connection if discovery hasn't found them yet).
 **Discovered peers on server**|  Only while you’re on a server: the people on it who share your password and whom you haven’t connected to yet. See Servers.| Connects you to them. They move to Connected peers.

 There's also the **Add peer by IP (Alt+A)** button, which opens a small box for a computer name or address. It's useful for a first connection over a VPN, where discovery hasn't reached the other computer yet.

 #### The Add manual peer box

 Control| Shortcut| What it does
 ---|---|---
 **Peer IP address or hostname**|  —| Type the address or computer name of the peer you want to add, for example a Tailscale address such as `100.64.0.2`. RemSound's usual port, 47830, is assumed; for a different one, type it after the address with a colon. Enter here does the same as OK.
 **OK**|  Alt+O, or Enter| Adds the peer in the box. From **Add peer by IP** on the Connectivity tab, RemSound looks the address up, adds it to your remembered peers and connects to it, or tells you if it can't find it. From the service profile, it adds the peer to the service's list, ticked. An empty box adds nobody.
 **Cancel**|  Alt+C, or Escape| Closes the box without adding anyone.

 ### Locking a profile to one exact address (and only that one)

 There are two ways to reach another computer, and the difference matters if that computer has more than one address:

   * **By name** — ticking someone in **Discovered peers not on a server**. RemSound found them from their announcement, and the entry shows their computer name. This is the easy, automatic way on an ordinary network.
   * **By a fixed address** — **Add peer by IP (Alt+A)** , then type the exact address, for example `10.8.0.1`.



 Normally RemSound is helpful about addresses, even one you typed: it can recognise that the address belongs to a computer it also hears announcing itself on the network, and if that computer turns up at a _different_ address — a new IP after a reboot, or a second address over a VPN — RemSound follows it there automatically so your sound keeps flowing. Most of the time that is exactly what you want.

 Sometimes you want the opposite: connect to one specific address and nothing else, _ever_. For that, tick **Lock to these exact peer addresses, no matter what (Alt+L)** on the Connectivity tab. It is off by default and is saved with the profile, so you can lock one profile down while another keeps the automatic behaviour. When it is ticked, for that profile:

   * RemSound uses **only the exact address you set** for each peer.
   * It will **not** look the other computer up by the name it advertises on the network, and it will **not** switch to any other address it discovers — even if that same computer is reachable at a second address at the same moment.
   * If the address you set stops working — the computer is off, the network is down, or it has moved to a new address — the connection **waits, or drops, until that exact address is reachable again**. RemSound will not go looking for another way through.



 **When you use this, set your peers by their IP address** (Add peer by IP, then type the address). That is the setting for a machine that appears under two addresses when you only ever want one of them: add that one IP, tick the box, and save the profile — the connection will use that address and never wander.

 With the box left unticked, RemSound behaves as it always has: the profile remembers exactly what you ticked, reconnects by that name or address next time, and quietly follows a peer that genuinely moves to a new address.

 ### You only hear peers you've ticked

 Even if a peer is sending sound your way, you won't hear it until you've ticked their checkbox. This is deliberate — connecting is a step where you give your consent. A peer’s name appears in Discovered the moment they come online, but they can’t make any sound on your speakers until you say yes — or, if you’ve set Accept connections from other peers to Automatically, until they tick you, which is you having said yes in advance.

 ### Connection health

 For each connected peer, the status read-out at the bottom of the window shows a small health note: the latest round-trip time in milliseconds, or **pending** , **stale** or **unreachable** if the regular check-in messages have stopped. (Round-trip time is how long sound takes to travel to the other computer and back.) RemSound plays a connect cue (a short sound) when a peer becomes healthy and a disconnect cue when one becomes unreachable. You can silence both cues using **Audio cue sounds** in the Preferences dialog (Options → Preferences, or Ctrl+P).

 ## 12\. How the network works

 RemSound uses two network **ports**. A port is a numbered door on the computer that sound and messages go in and out through. Routers and firewalls talk about ports too, so that is the word used here.

 Port| Purpose| Default
 ---|---|---
 Audio| The actual sound, sent straight from one computer to the other. The regular health check-ins use this same port too — one port, one firewall rule.| 47830
 Discovery| “I'm here” announcements every 1.5 seconds, so peers can find each other.| 47821

 You may also see **47831** in use. That is RemSound talking to its own DAW plugin, inside your computer only — it never goes on the network, so never open it in a firewall.

 One audio port is used for everything — Tailscale, local network connections, and any server. You don't normally type a port after an address; 47830 is assumed. Both sides of a connection do need to use the same audio port.

 ### Servers

 A server is a place several people meet. Everyone connects to it instead of to each other, which is how you get round a router that won't let anyone in, and how more than two of you can be in the same room at once.

 There's no public RemSound server. You run your own on a Linux machine or a Raspberry Pi: the code and its setup instructions are in the [server folder of the RemSound repository](https://github.com/Ednunp/RemSound/tree/main/server) on GitHub.

 **Getting on one.** On the Connectivity tab, press **Connect to server (Alt+N)**. A small window opens. It has a **Server address** box (Alt+A), which starts with the server you're on, or else the last one you used, and a **Remembered servers** list (Alt+M). Then the **Connect** button (Alt+C; while you're on a server it says **Disconnect** , Alt+D), **Close** (Alt+L), and a line saying what's happening. Type the address and press Enter or Connect, or arrow to one in the remembered list and press Enter (that connects only when you're not already on a server). If a server uses a port other than 47830, type it after the address with a colon, for example `myserver:47832`. Delete on the remembered list forgets a server. To empty the whole list, use **Clear remembered servers list** (Alt+S) on the Connectivity tab of Preferences.

 Once you're on, the same button in that window says **Disconnect** , and the button on the tab says **Disconnect from or change server**. To move to a different server, open the window, press Disconnect, type the new address and press Connect — the button goes back to Connect as soon as you've left. Close shuts the window; it never undoes anything, because everything happens when you press a button, not when you leave.

 #### The server window

 Control| Shortcut| What it does
 ---|---|---
 **Server address**|  Alt+A| The address or name of the server to connect to. It starts with the server you're on, or else the last one you used. If the server uses a port other than 47830, type it after the address with a colon, for example `myserver:47832`. Enter here presses the Connect button (Disconnect while you're on a server).
 **Remembered servers**|  Alt+M| The servers you've been on before, newest first, shared by all your profiles on this computer. Enter on one puts it in the address box and connects to it, but only when you're not already on a server. Delete forgets the highlighted server.
 **Connect** (says **Disconnect** while you're on a server)| Alt+C (Alt+D while it says Disconnect), or Enter| Connect goes to the server in the address box; unless you've turned it off, a message first reminds you that you'll only see people with your password. Once you're on, the button says Disconnect, which leaves the server and lets go of everyone you had ticked there. Either way your profile then has unsaved changes, because a profile remembers the server you were on.
 **Close**|  Alt+L, or Escape| Closes the server window. It undoes nothing: you stay on the server, or off it, just as you were.

 **Who you see.** While you're on a server, a list appears on the Connectivity tab called **Discovered peers on server (Alt+0)** , right under the ordinary **Discovered peers not on a server (Alt+D)** list. It's only there while you're connected. It shows the people on that server who have the same password as you — nobody else, ever. RemSound says so each time you connect, in a message with a “don't show this message again” tick. If your profile has no password you’ll see nobody at all, because a server puts people together by their password; the line beside the server button says so. And if the server doesn’t answer within about ten seconds, that line says it isn’t answering.

 #### The “Connecting to a server” message

 Control| Shortcut| What it does
 ---|---|---
 **Server notice**|  —| A read-only message shown each time you connect to a server yourself: on a server you only see, and can only connect to, the people whose password is the same as the one in your profile. Tab into it and arrow through it to read it again.
 **Don't show this message again**|  Alt+D| Tick it and this message isn't shown again when you connect to a server, on this computer, whichever profile you use. Off by default. It takes effect when you close the message.
 **OK**|  Alt+O, Enter or Escape| Closes the message, and RemSound goes on connecting to the server. If **Don't show this message again** is ticked, you won't see the message when you next connect.

 **Ticking.** Tick people in that list exactly as you tick anyone else. As always you both have to tick each other before any sound flows; until they tick you back, the line beside the Connect button says you're waiting for them. Everyone you tick is a peer like any other: their own volume, pan and EQ, their own recording track, their own place in the DAW plugin. In your peer lists they read as “their name (on the server)”, so you can tell them apart from the same person reached over your network.

 **Where they go once you've ticked them.** Out of this list and into **Connected peers** , exactly as somebody on your own network leaves the discovered list when you tick them. Both lists mean the same thing: people you haven't connected to yet. To let a server person go, untick them in Connected peers — that drops the server's tick with it, so RemSound stops sending to them and they come back to the server list, ready to tick again.

 **The same machine twice.** If the same computer is reachable both ways — on your network and through the server — and you tick both, you get two entries carrying the same sound twice over. Both rows say so: “same machine twice, also connected on your network” and “same machine twice, also connected through the server”. Untick whichever one you don't want.

 **When somebody goes.** Someone who leaves the server drops out of the list, marked **(gone)** for about 15 seconds first so a screen reader isn't reading a line that vanishes under it. Somebody you've ticked is in **Connected peers** instead, marked **(offline)** until they come back, and never in both lists at once. Each person on a server has their own connect and disconnect sounds, just like anybody on your network: the connect sound once you've both ticked each other and they answer, and the disconnect sound when they leave or untick you. Connected peers shows each of them as connected only while they themselves answer. If the SERVER goes — restarted, unplugged, off the end of your connection — everyone on it drops out the same way after about five seconds, and they all come back when it does.

 **A server by name.** If a profile connects to a server by its name, such as `myserver.example.com`, and the name can't be looked up when the profile starts — because the network isn't up yet, say — RemSound keeps trying every 30 seconds, and straight away when the network changes, and connects as soon as it can. Saving the profile meanwhile keeps the server in it. The same goes for a peer you added by name. And if a server by name stops answering for 30 seconds, RemSound looks the name up again: if it now leads to a new address, because the server's home connection was given a new one, RemSound follows it there on its own. The lock-screen service does the same.

 **One Windows account, one person.** Every copy of RemSound you run from your Windows account is the same person on a server, and so is the lock-screen service if you set its profile up from your account. So a tick on you, and any volume, pan or EQ somebody has given you, stays yours whichever of them is running. Two Windows accounts on one computer are two people, and two computers are always two people, even when they share a RemSound folder through something like Dropbox. The account that first runs RemSound 6.0 on a computer keeps that computer's existing place on servers, so nobody has to tick it again. If the lock-screen service was set up before 6.0, save its profile once from your account (Service, Configure service profile, Save and Close) so that it is you on the server.

 **What it costs you.** You send one copy of your sound to the server and the server passes it to each person due it. A room of six costs you no more to send than one person does. It's the server's connection that does the work, not yours.

 **Changing your password while you're on one.** That puts you among a different set of people, so everyone you had ticked is dropped and the list starts again. RemSound says so out loud when it happens.

 **Phones and older apps.** The iPhone and Android apps, and RemSound before 6.0, don't know about any of this. Through a server they still reach one person, the same way two people always could. That person appears in your list as “Someone on a phone or an older app”, and you tick them like anyone else — until you do, they hear nothing from you. What happens when one turns up follows your Accept connections from other peers setting: you're asked, it's ticked for you, or (on Manual) you tick it yourself.

 **Two things worth knowing.** If you type a server's address into “Add peer by IP” by mistake, RemSound notices and offers to connect to it as a server instead. And if you reach someone through a server, don't also tick them directly in your peer list — their audio would arrive by both routes and you'd hear them twice.

 The health check-ins travel on the same port as the audio, so if your sound reaches the other computer, your check-ins do too — one firewall rule covers both.

 ### Network priority

 RemSound automatically asks Windows to treat its audio as high-priority traffic, which helps most on a busy Wi-Fi network where other devices are streaming, downloading or video-calling. There's nothing to set up — it happens on its own every time RemSound starts. This helps on your local network and your home Wi-Fi; it makes no difference once the traffic leaves your home, but it does no harm either.

 ### On the same network (Wi-Fi or cable)

 On a normal home network, finding peers and checking their health both work with no setup. Start RemSound on two computers and they'll see each other within a second or two. You usually don't need to change any firewall settings.

 ### Computers in different places

 Connecting two computers directly across the internet needs one of these:

   * A VPN that puts both computers on the same private network — **Tailscale** is the one we recommend. (A VPN is a service that creates a private network linking your computers wherever they are.) Each computer gets a Tailscale address (it looks like `100.something`) and they can reach each other directly.
   * Or, let RemSound ask your router to open the audio port for you automatically — see Automatic router port opening (UPnP) below. Off by default; one tick to turn it on.
   * Or, port forwarding on each end's router by hand (this is more involved and isn't covered here).



 ### Automatic router port opening (UPnP)

 Most home routers support a feature called UPnP (or its newer cousins NAT-PMP and PCP) which lets an app ask the router to open a port so the outside world can reach it. RemSound can use this so two computers can find each other across the internet without you having to log into the router and set up port forwarding by hand.

 **How to turn it on.** Open **Options → Preferences** (Ctrl+P), go to the **Connectivity** tab and tick **Automatically open my router for incoming connections (UPnP)** (Alt+O). Off by default — we don't want to poke your router without permission. As soon as you tick the box, a status line appears just below it telling you what happened:

 Status line says…| What it means| What to do
 ---|---|---
 “Searching for a router that supports UPnP / NAT-PMP / PCP…”| RemSound is asking around on your network for a router that speaks one of these languages. Usually finishes within a few seconds.| Wait a moment.
 “Router port opened. Peers can reach you at X.X.X.X:47830.”| Your router has agreed to forward incoming audio to this computer. Tell the peer at the other end that address and they can connect using _Add peer by IP_.| Pass that address (the part before the colon) to whoever you want to connect to.
 “No router with UPnP / NAT-PMP / PCP found.”| Either your router doesn't support it, the feature is turned off in the router's settings, or something on your network is blocking it.| Try turning UPnP on in your router's settings page (look for “UPnP” or “NAT-PMP”), or use Tailscale instead.
 “The router opened the port, but the external address is on a carrier-grade NAT.”| Your router did its part, but your internet provider has put you behind a second layer of NAT (a sort of giant shared router) and there's nothing your home router can do about that. This is common on mobile broadband and on some cable connections.| Use Tailscale, or a server you run yourself, instead — both work fine through carrier-grade NAT.
 “The router rejected the port-mapping request.”| The router found the request but said no — usually because another device on your network already has the same port forwarded, or because the router has UPnP set to a restrictive mode.| Check your router's UPnP settings, or fall back to manual port forwarding or Tailscale.

 **Keeping it open.** Routers only open the port for a limited time, so while the box is ticked RemSound asks again every 15 minutes. If the router stops agreeing, the status line stops saying the port is open and shows RemSound looking for the router again; if the router can't be found, RemSound keeps looking every 15 minutes. If you untick the box while RemSound is in the middle of asking the router again, the port is still closed once the router answers.

 **Across sleep and reboots.** If your computer goes to sleep, RemSound asks the router to reopen the port automatically when it wakes up — some routers drop their port-forwarding list during long idle periods. Closing RemSound tells the router to forget the forwarding rule, so the port doesn't stay open after you're done.

 **Why this is off by default.** Some networks — corporate offices, shared accommodation, hotel Wi-Fi — really don't want apps asking the router to open ports for them, either because there's a security policy or because the router is locked down. Off by default means RemSound never touches your router unless you explicitly tick the box.

 ### Finding peers on Tailscale and other VPNs

 The ordinary “I'm here” announcements that work on a home network don't travel across a VPN. RemSound works around this by also sending announcements directly to every address in your Remembered peers list. So:

   1. One time only: each side adds the other's Tailscale address to its Remembered peers list (using the “Add peer by IP” button).
   2. From then on, RemSound sends announcements straight to those addresses every 1.5 seconds.
   3. The other side hears the announcement, adds the sender to its own list, and announces back.
   4. Within seconds, both sides see each other in Discovered peers not on a server, with no further typing.



 So the rule is: **only one side has to type the other's address once.** After that, the discovery works both ways on its own.

 ### Round-trip time and what it means

 Round-trip time is how long it takes for sound to travel to the other computer and back.

 Round-trip time| What you'll experience
 ---|---
 0–2 ms| The same computer talking to itself.
 2–10 ms| Same local network. Effectively instant.
 15–40 ms| Typical for Tailscale or modern broadband-to-broadband. Comfortable for conversation.
 50–100 ms| Tailscale, or a server, or one end on Wi-Fi a long way off. Still usable, but you start to notice it for music.
 100 ms+| Something is wrong, or you're talking across the world. Playing music together is hard.

 ## 13\. Passwords and encryption

 **All the audio RemSound sends is encrypted** — scrambled as it leaves your computer and only unscrambled at the other end. Anyone in between (your internet provider, a shared Wi-Fi, anyone watching the connection) just sees noise. This means you no longer need a VPN just to keep your audio private. And it adds no delay you could ever notice — the scrambling happens in millionths of a second, far less time than the audio itself takes.

 ### How it works: a password per profile

 Every profile carries a **password** , and that password is the key. The rule is simple:

   * **Same password on both ends →** you connect and hear each other.
   * **Different passwords →** no audio passes, and RemSound tells you so (see below) rather than leaving you with mysterious silence.



 So the password does double duty: it both encrypts your audio and decides who you can talk to. You and the person you're connecting with agree a password — say it out loud, or text it to each other — and each set it on the profile you use to talk to one another. The profile names don't have to match; only the passwords do.

 ### Setting and changing passwords

 Where| What it does
 ---|---
 **When you create a profile**|  Saving a new profile (File → Save as) asks you for a password right then.
 **File → Change this profile's password** (Alt+F, P)| Changes the password on the profile you're using now. The box shows the current password in plain, readable text — so a screen reader reads the actual characters, not a row of dots — and you type a new one over it.
 **Options → Profile passwords**| A list of every profile with its password in an editable box: a one-stop password manager. Edit any of them and press OK to save them all.

 If you try to start sending or receiving on a profile that has no password yet, RemSound asks you to set one first (and offers to remember it on the profile so you don't type it again next time). Audio can't flow without a password — encryption is always on, there's no “off” switch.

 #### The Change profile password box

 Control| Shortcut| What it does
 ---|---|---
 **Password for profile** (followed by the profile's name)| —| The profile's password, in plain text so your screen reader reads the actual characters; type a new one over it. You and the person you connect to must use the same password. A longer one is best, such as three unrelated words with a number, like `kettle9tiger42moon`. Spaces at either end are ignored.
 **OK**|  Alt+O, or Enter| Keeps the password in the box. From File → Change this profile's password, it is used at once and written into the profile file straight away, without saving your other changes; an empty box removes the password, and no sound can flow until you set one. When RemSound has asked for a password because sound can't flow without one, an empty box is refused.
 **Cancel**|  Alt+C, or Escape| Closes the box without changing the password. If RemSound asked for one because sound can't flow without it, nothing is sent or received until a password is set: ticking Send my audio or Receive audio asks again.

 #### The Profile passwords window

 Control| Shortcut| What it does
 ---|---|---
 **Password for profile** (one box for each profile, named after it)| —| That profile's password, in plain text so your screen reader reads the actual characters. Type a new one over it and press OK to save it into that profile. You and the person you connect to must use the same password; an empty box removes it.
 **OK**|  Alt+O, or Enter| Saves every password you changed in this window into its profile's file, touching nothing else in those profiles, and closes the window. If the profile you're using now was one of them, its new password is used straight away. If a password can't be saved — because its profile's file has been moved or deleted, say — RemSound tells you which, and still saves the rest.
 **Cancel**|  Alt+C, or Escape| Closes the window without saving any of the passwords you changed in it.

 **Use a strong password.** The password is the only thing protecting your audio from someone who records your network traffic, so it's worth picking a good one — three unrelated words with a number, like `kettle9tiger42moon`, is easy to type and remember and very hard to guess. RemSound suggests this when you set a password but doesn't force it, so any password you and the other person agree on will work. One honest note: the password is stored in the profile file in a recoverable form (so profiles can sync between your own machines) — anyone who can read your profiles folder can read the passwords, so treat that folder accordingly.

 ### When passwords don't match

 If you connect to someone whose password is different from yours, RemSound shows a clear message — _“ You and 192.168.1.5 have different passwords, so no audio will pass between you”_ (it names the other computer by its address) — so you know exactly what to fix. If the other person has no password set on their profile, RemSound tells you they aren't sending a password fingerprint. (A password fingerprint is a code worked out from the password, so the two computers can tell whether their passwords match without sending the password itself.) That is by far the most common reason. Less often, their copy of RemSound is old enough to predate encryption and needs updating. Check the password first.

 ### Two things worth knowing

   * **Very old copies can't join in.** A copy of RemSound old enough to predate encryption can't talk to a current one. Anyone still using one needs to update.
   * **The password lives with the profile.** It's stored (lightly scrambled) inside the profile file, so it travels with the profile if you copy it to another machine or sync it through something like Dropbox. That's handy, but it means you should keep the profile file private — protect it the way you'd protect the password itself.



 ## 14\. Latency and audio quality

 Latency is the small delay between sound leaving one computer and arriving at the other. Four controls together shape the trade-off between latency and sound quality, all on the Audio profile tab:

   * **Audio jitter buffer in milliseconds (Alt+L)** — the main target for how much sound the receiving side keeps in reserve.
   * **Buffer smoothness (Alt+B)** — how hard the receiving side works to protect against sudden jitter.
   * **Packet size (Alt+P)** — Standard or Small. With PCM or Opus broadcast quality, Small packets shave a little off the sending delay, but double how many packets are sent. Opus live latency is already as small as it goes.
   * **Continuous auto-tune** — lets the receiving side choose the jitter buffer for you, re-checking every few seconds.



 Plus the codec choice (PCM, Opus broadcast quality, or Opus live latency), also on the Audio profile tab. Most people only need to pick a codec and a smoothness level and leave the rest at the default.

 ### The sound-card cushion is automatic

 Separately from the controls above — which manage the cushion against _network_ jitter — RemSound also keeps a small cushion at the sound card itself, to smooth over the tiny timing differences between your two computers' sound clocks. RemSound sizes that cushion to each card automatically: a card that moves sound in bigger chunks (some onboard and USB cards do) gets a little more room, while a fast professional interface stays tight. You don't set this or think about it — it settles on the right amount for whatever card you're using.

 ### The jitter buffer

 The **jitter buffer** tells the receiving side how much sound to keep in reserve as a cushion against uneven network timing. It used to be called “audio latency”, which was misleading: it is only one part of the delay you hear, and the only part RemSound controls. A bigger cushion means more delay but fewer clicks. A smaller cushion means less delay but more clicks when the network wobbles.

 **You can change it while you're listening.** Move the control and the delay follows within a few seconds — there's no gap or click while it changes. Lowering it takes effect straight away. Raising it can't happen instantly, because the extra cushion has to be built up out of the sound still arriving, so RemSound plays very slightly slow for a moment while it banks the difference: a big jump takes a few seconds to arrive and you can hear it stretch out as it goes. That's the change happening, not a fault.

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

 Smoothness and the jitter buffer work together — smoothness controls _how the receiving side reacts_ when sound runs late; the jitter buffer controls _how big a head-start it builds up_. A practical tip: if you can hear clicks, try raising smoothness by one or two before you reach for a bigger jitter buffer.

 ### Packet size — Standard or Small

 Two choices: **Standard** (the default) and **Small**. This controls how much sound each network packet carries:

 Packet size| What changes| Pick when
 ---|---|---
 Standard| One audio packet every 5 ms with PCM, every 20 ms with Opus broadcast quality, or every 2.5 ms with Opus live latency.| Any internet or Tailscale connection — any time you don't have a guaranteed-clean local network.
 Small (local network only)| Halves the packet: PCM goes from 5 ms to 2.5 ms, and Opus broadcast quality from 20 ms to 10 ms. Opus live latency is already as small as it goes, so Small makes no difference to it.| A same-house local network over wired Ethernet, where the network isn't going to drop packets or jitter.

 The saving is small: half a packet's worth of sound, so 2.5 ms with PCM and 10 ms with Opus broadcast quality. Small packets are useful when you and your collaborator are on the same local network and want to chase every last millisecond. For any internet connection it's a false economy, because twice as many packets means twice the chance of one arriving late, which you hear as clicks.

 ### Locking to the audio clock (automatic)

 RemSound always ties its sending timing to the sound device's own hardware clock, instead of letting Windows decide the pace. This used to be a **Lock to audio clock** checkbox that was off by default; it is now always on and there is nothing to set, because turning it off only ever added delay. It works the same whether you use WASAPI alone or WASAPI and ASIO together: each sound card sets its own pace.

> **Why it matters:** Windows' own timer can be several milliseconds late, even at top priority. At delay settings under about 15 ms, you'd hear that as clicks. Following the sound card's clock avoids it — the sound card itself sets the pace.

 ### Total latency: what the box is telling you

 Beside the jitter buffer there is a read-only **Total latency** box (Alt+M). It reports three things, and they are three different measurements rather than a target and a score:

   * **Jitter buffer** — what the control above is set to. The only part RemSound governs, and the part auto-tune moves.
   * **Sound card and hardware add** — everything else in the chain: capturing the sound, packing it up, the network itself, and your sound card's own output buffer. RemSound cannot give any of this back. It is measured live, not a fixed figure, so it moves by a few milliseconds as your machine works.
   * **Total latency** — the two added together, and shown as _approximately_ for the reason below. This is _one way_ : from their microphone to your ears, not there and back.



 #### Why it says “approximately”

 Because most of that total is worked out rather than timed, and it would be wrong to hand you a figure that looks more certain than it is. The five stages behind it:

   * **The jitter buffer** is the figure you set, which is what you hear once the buffer has filled.
   * **Capturing the sound** is measured — how often your input device actually hands audio over. Until it has a reading it falls back to the 10 ms RemSound asks Windows for.
   * **Packing it for sending** is measured too — how often packets actually leave. If that can't be timed yet, it is worked out from the codec and send rate you chose, which is exact for those settings.
   * **The network** is a real measurement — the round trip to that peer — halved. That assumes the trip out takes as long as the trip back, which is usually close but not guaranteed.
   * **Your sound card's output** is a real measurement of how often it asks for audio, doubled. The doubling reflects how these devices normally buffer, rather than something RemSound observes directly.



 So three of the five are live measurements from your own machine, one is the network measured and halved, and none of it is a figure typed in and left there. It is still called approximate because of the halving and the doubling above, both of which are reasonable assumptions rather than observations.

 It has matched what people actually hear. It is not a stopwatch. If you need a precise figure — for lining up a recording, say — measure it rather than reading it here.

 So a jitter buffer of 20 ms with a total of 60 ms does not mean anything is failing to reach 20. It means the cushion is 20 and your equipment and connection account for the other 40. Auto-tune cannot make the total equal the buffer, because most of the total was never the buffer.

 ### Continuous auto-tune

 The **Continuous auto-tune jitter buffer** checkbox hands the jitter buffer over to RemSound itself. When it's on, RemSound watches how evenly packets are arriving, every few seconds, and nudges the jitter buffer up if it’s seeing late packets, or down if the network has been calm. It deliberately ignores a single one-off stall — the kind a driver or Windows hiccup causes once and never again — and only raises the cushion when late audio keeps arriving, so one brief blip doesn't balloon your latency for the rest of the session. The **Auto-tune interval for jitter buffer (Alt+N)** list beside it sets how often it re-checks — **3, 5, 10, 15, or 30 seconds** , with 5 as the default. Faster values react quickly to a change in the network but can feel a bit twitchy. Think of continuous auto-tune as a hands-off way to keep the cushion the right size as your network changes through the session.

 If you turn auto-tune off, the jitter buffer just stays wherever it last was.

 Auto-tune moves in both directions. If the buffer keeps running short — which is what happens when you set it lower than your equipment can manage — it raises it to the smallest value it has found that stays clean, rather than leaving you with a setting that cannot work. It learns that value by experiment on your own machine, so it never has to guess.

 ### Artefact sound type

 When the playback reserve briefly runs empty, RemSound has to fill the gap with something. The **Artefact sound type** list decides what that gap sounds like:

   * **Noise burst (default)** — a brief soft hiss that blends into music. Easy on the ear; it tells you something happened without being jarring.
   * **Click** — the gap is left unfilled, so you hear an obvious click each time. Use this when you want to _hear_ every problem (for example, while you’re tuning the jitter buffer down).



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

 ## 15\. Keyboard shortcuts (within the main window)

 Each tab has its own Alt+letter shortcuts. The same letter can do different things on different tabs without clashing — the shortcuts only work on the tab that's showing. Move between tabs with Ctrl+Tab and Ctrl+Shift+Tab, or jump straight to one with Ctrl and its number (Ctrl+1 for the first tab, and so on — the numbers follow whatever order you've set the tabs in).

 ### Connectivity tab

 Key| Action
 ---|---
 Alt+C| Go to the Connected peers list
 Alt+E| Go to the Peer details box (for the highlighted connected peer)
 Alt+M or F2| Rename the highlighted connected peer (F2 matches the Windows Explorer rename key)
 Alt+D| Go to the Discovered peers not on a server list
 Alt+R| Go to the Remembered peers list
 Alt+A| Add peer by IP
 Alt+L| Toggle Lock to these exact peer addresses
 Alt+0| Go to the Discovered peers on server list (only while you’re on a server)
 Alt+N| Connect to server, or Disconnect from or change server
 Alt+S| Go to the Connection status read-out

 (The logging controls — Enable logs, Write logs now and the log-folder housekeeping — are on the Logging tab of the Preferences dialog; reach it via Options → Preferences or Ctrl+P, then use Alt+L / Alt+W within the dialog.)

 ### Audio inputs and outputs tab

 Key| Action
 ---|---
 Alt+U| Uncheck all inputs and outputs on all soundcards and set the ASIO driver to none
 Alt+D| Go to the ASIO driver list (hidden if no ASIO drivers are installed)
 Alt+R| Toggle Receive audio
 Alt+1| Go to ASIO outputs for received audio
 Alt+2| Go to ASIO audio inputs to send
 Alt+3| Go to WASAPI outputs for received audio
 Alt+4| Go to WASAPI audio outputs to send
 Alt+5| Go to WASAPI audio inputs to send
 Alt+6| Go to the “How to send WASAPI audio” chooser (whole devices vs specific applications)
 Alt+8| (Applications mode) Go to the Currently active applications list
 Alt+9| (Applications mode) Go to the Remembered applications list
 Alt+V| Go to the volume slider
 Alt+S| Toggle Send my audio

 ### Audio profile tab

 Some of these shortcuts shift depending on whether an ASIO driver is chosen. When one is chosen, the ASIO jitter buffer takes Alt+I and its auto-tune takes Alt+T, and the WASAPI-path controls move to Alt+W / Alt+Y so they don't collide.

 Key| Action
 ---|---
 Alt+U| Toggle Use CPU and Windows performance settings in high priority mode (for this profile)
 Alt+C| Go to Audio codec
 Alt+P| Go to Packet size
 Alt+L| Go to the jitter-buffer control. With no ASIO driver this is the single **Audio jitter buffer in milliseconds** box; with an ASIO driver chosen the WASAPI box takes Alt+W and the ASIO box takes Alt+I instead
 Alt+T| Toggle continuous auto-tune — the ASIO path when an ASIO driver is chosen, otherwise the single Continuous auto-tune toggle
 Alt+W| (Only when an ASIO driver is chosen.) Go to the WASAPI-path jitter-buffer control
 Alt+Y| (Only when an ASIO driver is chosen.) Toggle the WASAPI-path continuous auto-tune
 Alt+I| (Only when an ASIO driver is chosen.) Go to the ASIO-path jitter-buffer control
 Alt+N| Go to the Auto-tune interval list — it sets how often the jitter buffer is re-checked. It drives the timing for the WASAPI auto-tune and, when an ASIO driver is chosen, the ASIO auto-tune too — one list, both paths. Each path still settles at whatever jitter buffer its own calculation chooses; only the timing of the re-checks is shared. The label reads “Auto-tune interval for jitter buffer” when only one kind of output is ticked, and “Auto-tune interval for WASAPI and ASIO jitter buffer” when both are.
 Alt+M| Go to the Total latency readout
 Alt+B| Go to Buffer smoothness
 Alt+A| Go to Artefact sound type

 ### Volume, pan and EQ for peers tab

 Present whenever the Volume, pan and EQ for peers tab is showing (it's shown by default; the toggle is “Show the volume, pan and EQ for peers tab” on the Appearance tab of Preferences). See Volume, pan and EQ for peers tab.

 Key| Action
 ---|---
 Alt+E| Toggle Enable volume, pan and EQ for all peers
 Alt+U| Go to the Peers checklist
 Alt+L| Go to the Volume slider
 Alt+N| Go to the Pan slider
 Alt+Q| Set peer EQ to default
 Alt+M| Go to the EQ mode picker
 Alt+A| Add band (parametric EQ mode only)
 Alt+B| Go to the Bands list (parametric EQ mode only)
 Alt+D| Delete band (parametric EQ mode only)

 ### Menu shortcuts (work from any tab)

 Menu by menu, in the order the menus appear on the menu bar. Items that are greyed out at the moment can't be picked.

 #### File menu (Alt+F)

 Key| Action
 ---|---
 Ctrl+N, or Alt+F, W| New profile
 Ctrl+O, or Alt+F, O| Open profile
 Alt+F, R| Recent profiles (submenu — then 1..5 for the matching slot)
 Ctrl+S| Save the current profile (or Save as if on a new profile)
 Alt+F, A| Save profile as
 Alt+F, M| Rename the current profile
 Alt+F, L| Lock profile (read-only), on or off
 Alt+F, P| Change this profile's password
 Alt+F, N| Minimise to tray
 Alt+F, X| Exit

 #### Record menu (Alt+K)

 Key| Action
 ---|---
 Ctrl+R, or Alt+K, R| Start or stop recording (toggles)
 Alt+K, O| Open the current recordings folder
 Alt+K, C| Change the recordings folder

 #### Service menu (Alt+J)

 Key| Action
 ---|---
 Alt+J, C| Configure service profile
 Alt+J, I| Install service
 Alt+J, U| Uninstall service
 Alt+J, T| Start service
 Alt+J, P| Stop service
 Alt+J, R| Repair service folder access
 Alt+J, V| View service log
 Alt+J, L| View service update log

 #### DAW plugin menu (Alt+G)

 Key| Action
 ---|---
 Alt+G, I| Install plugin (Reinstall plugin once it's installed)
 Alt+G, R| Remove plugin
 Alt+G, Q| Apply pan and EQ to plugin audio, on or off
 Alt+G, L| Let plugins connect to RemSound, on or off

 #### Options menu (Alt+O)

 Key| Action
 ---|---
 Alt+O, S| Recording settings
 Ctrl+K, or Alt+O, K| Keyboard shortcuts
 Alt+O, W| Profile passwords
 Alt+O, N| Manage named peers
 Alt+O, E (or Alt+O, D)| Enable (or Disable) Realtek ASIO driver in RemSound — only there if a Realtek ASIO driver is installed
 Alt+O, I (Alt+O, U once installed)| Install RemSound on this PC (or Uninstall RemSound from this PC)
 Ctrl+P, or Alt+O, P| Preferences

 #### Help menu (Alt+H)

 Key| Action
 ---|---
 Shift+F1, or Alt+H, H| Open this manual
 Alt+H, C| Check for updates
 Alt+H, A| About RemSound

 ### Always available

 Key| Action
 ---|---
 **F1**| **Context help** for the control you're on. Escape closes it. See Context help.
 **Shift+F1**| **Open this manual** in your default web browser. Works anywhere in RemSound — the main window, every dialog, and the profile picker on first launch.
 Ctrl+Tab / Ctrl+Shift+Tab| Move to the next / previous tab
 Ctrl+1…Ctrl+9| Jump straight to a tab by its position — Ctrl+1 is the first tab, Ctrl+2 the second, and so on. The number follows the current order, so if you reorder the tabs (Preferences → Appearance) the numbers move with them. Works in the main window and in the Preferences dialog.
 Tab / Shift+Tab| Move between controls within the current tab
 Spacebar| Tick or untick an item in any device list, or toggle the focused checkbox
 Up / Down| Move between items in any list
 Alt+F4| Close (the standard Windows shortcut)

 ## 16\. Global hotkeys (work even when minimised)

 Your keyboard shortcuts are **shared across all your profiles** — set one once and it works on every profile, and stays put when you switch between them.

 You set these up in the Keyboard shortcuts dialog (Ctrl+K, or Options → Keyboard shortcuts). The dialog is a single list of every hotkey you can set: **Enter** sets the highlighted row, and **Del** — or the **Clear this shortcut** button (Alt+C) — clears it (back to _not set_). When you're setting a shortcut, you can press **Delete** inside that box to leave it unassigned. **Escape** or the **Close** button (Alt+O) closes the dialog. The rows, in the dialog's order, with their defaults:

 Hotkey| Action| Default
 ---|---|---
 Toggle sending audio| Turns **Send my audio** on or off, exactly as ticking or unticking its box does.| Ctrl+Shift+Alt+S
 Toggle receiving audio| Turns **Receive audio** on or off, exactly as ticking or unticking its box does.| Ctrl+Shift+Alt+R
 Show or hide window| Shows or hides the main window.| Ctrl+Shift+F10
 Volume up / down for received sound on this machine| Adjust this computer's received-sound volume, 5 points a press. It counts as a change to your profile, just like moving the slider, so it's saved with the profile.| Unset
 Start / Stop recording| Start or stop a recording on this computer. The same toggle as the Record menu's start/stop item and the in-app Ctrl+R, but it works system-wide (RemSound doesn't need to be the active window). See Recording to a file for what gets captured.| Unset
 Send remote RemSound volume up to peers| Tell every connected peer to raise their RemSound volume slider by 5 points (only obeyed by peers that have ticked “Accept remote volume commands”). It doesn't change your own volume. See Remote control.| Unset
 Send remote RemSound volume down to peers| The same, but lowering.| Unset
 Send remote RemSound receive mute toggle to peers| Tell every connected peer to toggle their RemSound receive mute.| Unset
 Send Windows global volume up to peers| Tell every connected peer to nudge their _Windows_ volume up by one step (about 2%, the same as their keyboard volume key). This affects every app on the receiving computer, not just RemSound. Hold the hotkey down for bigger jumps. See Remote control.| Unset
 Send Windows global volume down to peers| The same, but lowering.| Unset
 Send Windows global mute toggle to peers| Tell every connected peer to toggle their Windows mute.| Unset
 Quick profile switch (open a list of all profiles)| Pop up a list of all your profiles and switch to one — works from anywhere, even with RemSound in the tray (where it stays after the switch). See _Quick profile switch_ below.| Unset
 Speak the RemSound status information from anywhere (screen reader only)| Read the whole status line out loud through your screen reader — the connection time, how many peers you have, whether sound is flowing, and how healthy the link is — from anywhere, even with RemSound in the tray. Just for screen-reader users; see Hearing the status on demand below.| Unset
 Toggle volume, pan and EQ for all peers| Flip the one master switch on the Volume, pan and EQ for peers tab from anywhere, so you can drop all your per-person shaping in and out without leaving the app you're in. See that tab for what the switch does.| Unset

 You can change any of these to whatever combination you prefer. Each accepts modifiers (Ctrl, Shift, Alt) plus one ordinary key.

 #### The Keyboard shortcuts dialog

 Control| Shortcut| What it does
 ---|---|---
 **Keyboard shortcuts** (the list)| —| Every global hotkey RemSound has, each read as its action and its current key, or “(not set)”. Press Enter on one to set a new key, or Delete to clear it. A change works at once, anywhere in Windows, and is kept on this computer for all your profiles.
 **Clear this shortcut**|  Alt+C| Clears the key of the shortcut highlighted in the list, so that action has no hotkey. It happens at once, without asking. To give it a key again, press Enter on it in the list.
 **Close**|  Alt+O, or Escape| Closes the Keyboard shortcuts dialog. Every change you made in it has already taken effect and been kept, so there is nothing to save or undo.

 #### The Change hotkey window

 Control| Shortcut| What it does
 ---|---|---
 **Current hotkey**|  —| Hold the whole key combination you want — at least one of Ctrl, Alt or Shift, plus one ordinary key — then let go: the shortcut is set at once and the window closes. This box says what you're pressing as you press it. Plain Delete leaves the shortcut unassigned, and Escape closes without changing it. Tab moves on to the Cancel button.
 **Cancel**|  —| Closes this window without changing the shortcut. Tab from the hotkey box reaches it; then Space, Enter or Escape presses it. It has no Alt key, because every key combination pressed in the hotkey box is taken as the shortcut you're choosing.

 ### Quick profile switch

 Once you've given **Quick profile switch** a key, pressing it anywhere pops up a small list of every profile you have. Arrow to the one you want and press Enter (or click it) to switch to it. If your current profile has unsaved changes, you're asked whether to save them first; the question comes to the front even with RemSound in the tray. The profile you're currently on is marked in the list. A sound plays as the list opens, and the profile-switch sound plays as it switches. Press Escape to close the list without switching. If another RemSound window is open and waiting for you — Preferences, say — the hotkey says to close that first, and does nothing else.

 If RemSound was minimised to the system tray when you pressed the hotkey, it switches the profile and **stays in the tray** — the window doesn't jump up in front of whatever you're doing. So you can change profiles mid-task without losing your place.

 Control| Shortcut| What it does
 ---|---|---
 **Profiles**|  —| Every profile you have, with the one you're on marked “(current)” and already highlighted. Arrow to one and press Enter to switch to it; if your current profile has unsaved changes, you're asked whether to save them first. If RemSound was in the tray, it stays there after the switch.
 **Close**|  Alt+C, or Escape| Closes the list without switching profile.

 ### Hearing the RemSound status on demand

 The main window has a **status line** that updates every second with how long you've been connected, how many peers you have, whether sound is flowing, and how healthy the connection is. Normally your screen reader reads it like any other text — but now and then, for reasons that have nothing to do with RemSound, a screen reader loses sight of it and says there's nothing there.

 This hotkey is the cure. Give **Speak the RemSound status information** a key in the Keyboard shortcuts dialog, and from then on pressing it reads the whole status out loud, wherever you are — even when RemSound is tucked away in the tray or another program is in front. It's unset to start with, so the key is yours to choose.

 It reads the status out **a line at a time** — peers, ping, uptime, data rates, totals — so each piece lands as its own short phrase rather than one long run-on, and big totals show in gigabytes once they pass a gigabyte. A quick **double press** of the same hotkey **copies the status to the clipboard** instead of reading it, so you can paste it to someone if you're comparing notes.

 This one is just for screen-reader users: it talks straight through your screen reader. It works with the screen readers RemSound's speech helper supports — **NVDA** , JAWS, Window-Eyes, System Access, SuperNova and ZoomText — and falls back to Windows' own built-in speech if none of those is running. If you don't use a screen reader, just leave this one unset.

 ### Your screen reader reads out the hotkeys

 Once a hotkey is set, your screen reader reads it out whenever you land on the menu item or control it's tied to — for example, moving onto **File → Open profile** announces “Ctrl+O”, and a control with a global hotkey announces “press [your key] anywhere”. So you can learn and confirm your shortcuts just by arrowing around the window, without coming back to this dialog.

 ## 17\. Remote control: adjusting a peer's listening volume from your end

 Here's the situation this is for: you're on your laptop, listening to sound coming from your desktop, and you've got NVDA Remote open so you can drive the desktop using your laptop's keyboard. Every key you press goes to the desktop — including any volume key on the laptop, which now never reaches the laptop itself. There's no way from inside that NVDA Remote session to nudge the laptop's listening volume without breaking out of the session.

 RemSound's **remote control** feature gives you a way around this: you set up a hotkey on the desktop (the computer your keyboard is talking to) that sends a command across the audio link, telling the laptop's RemSound to raise, lower or mute its own listening volume. You stay in NVDA Remote, and the laptop responds.

 ### Two kinds of remote command

 There are two independent sets of remote-control hotkeys, both governed by the same opt-in toggle on the receiving end. Pick whichever fits the situation, or set up both:

 Set| What the receiving computer does| Best for
 ---|---|---
 **RemSound app volume**|  Adjusts the receiving peer's RemSound volume slider by 5 points per press, or toggles RemSound's receive mute. Only RemSound's sound is affected.| Fine adjustments while RemSound's slider still has room to move. Doesn't touch the screen reader's volume or any other app.
 **Windows global volume**|  Nudges the receiving peer's Windows master volume up or down by one step (about 2%, exactly the same as pressing the keyboard volume key there), or toggles the master mute. This affects every app on the receiving computer, including the screen reader. It always goes to whichever device is that computer's default output at the time, even if the default has changed since the last command.| Real-world “I need this louder” situations, especially with hearing impairment, or when RemSound's slider is already at the top. Hold the hotkey down to ramp up over a longer range.

 Both sets target the receiving computer. Neither one changes anything on the sending computer.

 ### How to set it up

   1. On the computer that should _respond_ to remote commands (the one you're listening on — the laptop in the example): open **Preferences** (Ctrl+P) and tick **Accept remote volume commands from peers** (Alt+V). Save the profile (Ctrl+S) so the choice sticks. (One toggle covers both kinds of remote command.)
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
   * Remote commands travel on the same audio port as the sound and the health check-ins (47830 by default), so there's no extra firewall rule to add.



> **Tip for troubleshooting:** the log file (Options → Preferences → Logging tab → Enable logs) records every remote-control command sent and received, including `IGNORED` entries when an incoming command was turned down — either because the sender wasn't in your list of ticked peers, or because “Accept remote volume commands” was off. Handy for working out “why isn't my hotkey doing anything” without guessing.

 ## 18\. Startup behaviour

 Startup behaviour is on the **Startup behaviour** tab of the Preferences dialog (**Options → Preferences**, or Ctrl+P). It has four independent toggles, plus a profile picker that appears when the third one is on. Each tick is saved straight away — there's no OK or Apply button.

 Toggle| What it does
 ---|---
 **Start minimised to tray (Alt+M)**|  RemSound hides itself in the system tray as soon as the main window finishes loading. The window is still reachable from the tray icon and the Show or hide window hotkey. Useful together with **Start RemSound automatically when this user logs in** , for a fully hands-off “turn the computer on, start streaming” setup. Off by default; the choice is saved straight away.
 **Start RemSound automatically when this user logs in (Alt+A)**|  Adds RemSound to (or removes it from) Windows' standard list of programs that start when you log in. After ticking it, Windows launches RemSound the next time you log in. It also appears under Task Manager → Startup, where you can disable it too. It applies to your account only — it doesn't need admin rights and doesn't affect anyone else who uses the same computer.
 **Start with a specific profile (Alt+P)**|  When ticked, RemSound skips the startup profile picker and loads the profile you choose in the **Profile to start with** list, which appears under this box. When unticked, the profile picker shows as normal. If you don’t have any saved profiles yet, ticking this shows a message and the box stays unticked — save a profile first, then come back. Unticking it forgets which profile was chosen, so when you tick it again, check the profile in the list.
 **Enable context help message at startup (Alt+H)**|  When ticked, RemSound tells you as it starts that context help is available: F1 for help on the control you're on, and Shift+F1 for this manual. Ticking **Do not show me this message again** on the message unticks this; tick it here to bring the message back. On until you hide the message.

 **Profile to start with (Alt+L)** — the list of your saved profiles. It only shows while **Start with a specific profile** is ticked. Pick a profile and RemSound loads it each time it starts, instead of showing the profile picker; the choice is saved straight away.

 ### Combining the three for a hands-off start

   1. Save a profile with the device choices, peers, and sound settings you want for “always-on” use.
   2. Go to the Startup behaviour tab in Preferences. Tick all three: _Start minimised_ , _Start automatically when this user logs in_ , and _Start with a specific profile_ — then pick the profile you just saved.
   3. Close the dialog. Reboot, or log out and back in, to test — RemSound starts itself, loads the profile, and goes straight to the tray. Sound starts flowing as soon as the peer is reachable.



> **Where these are stored:** the start-minimised choice and the start-with-profile name are kept in a small settings file on this computer. The auto-start toggle is kept in Windows' standard startup list — you turn it on or off from this dialog, or from Task Manager → Startup.

 ## 19\. Audio cue sounds

 RemSound plays a short sound at moments where you might want an audible confirmation that something just happened. These are called **cue sounds**. A range of events have a cue:

 Cue| Plays when
 ---|---
 **Connect sound**|  A peer goes from “trying” or “unreachable” to actually connected.
 **Disconnect sound**|  A previously-connected peer drops off (network blip, peer closed RemSound, computer went to sleep, etc).
 **Recording start sound**|  You start a recording.
 **Recording stop sound**|  You stop a recording.
 **Profile saved sound**|  A profile is saved — whether via File → Save or File → Save as.
 **Profile switched sound**|  You switch to a different profile — from the Recent profiles menu, the Quick profile switch popup, or File → Open profile. It plays the moment you pick the new profile (or, if you're asked about unsaved changes, once you've answered). It deliberately does _not_ play on a fresh start into your first profile, so it isn't layered on top of the connect sound at launch.
 **Profile menu open sound**|  The Quick profile switch popup opens.
 **Update sound**|  An update is about to install — it plays just before RemSound closes to update itself. Handy when updates install silently in the background, so you're not caught off guard when RemSound restarts. Plays whether you ran the update by hand or it installed on its own.
 **Startup sound**|  RemSound has finished starting up. It plays once at launch, even when RemSound opens straight to the notification area, so you know it's running.
 **Send turned on / off sound**|  You turn sending your audio on or off — whether by ticking the **Send my audio** box in the window or with the Toggle sending audio hotkey. There's a separate sound for on and for off.
 **Receive turned on / off sound**|  You turn receiving audio on or off — from the **Receive audio** box or the Toggle receiving audio hotkey. Again, a separate sound for on and for off.
 **Minimise (hide) sound**|  RemSound's window minimises to the notification area (hides).
 **Restore (show) sound**|  RemSound's window is brought back from the notification area (shows).
 **Checkbox ticked / unticked sound**|  You tick or untick _any_ checkbox anywhere in RemSound — a click for ticked, a different one for unticked. This gives instant feedback on which way a box just went, which is especially handy in the busy inputs and outputs lists. There's a separate sound for ticking and for unticking.
 **Switch tabs sound**|  You move between tabs anywhere in RemSound — the row of tabs in the main window, or the tabs in a dialog like Preferences. It plays each time you land on a different tab (with Ctrl+Tab, or the arrow keys when the row of tab names has focus).
 **Context help opened / closed sound**|  You press F1 and context help opens, and again when it closes. There's a separate sound for opening and for closing. The DAW plugin plays the same sounds when you use context help inside your music software.

 All of these cues play through your default Windows sound output, which is separate from the audio RemSound is sending or receiving. They don't appear in a normal recording. (The exception: if your sending side is capturing the very output device the cues play through, then they get captured along with everything else from that device.)

 ### The Audio cues tab

 Open **Options → Preferences** (or Ctrl+P) and go to the **Audio cues** tab. (Preferences is organised into seven tabs — General, Connectivity, Appearance, Audio cues, Startup behaviour, Update settings and Logging — which you move between with Ctrl+Tab, Ctrl and a number, or the arrow keys when the row of tab names has focus.)

 The **Audio cue sounds (Alt+N)** list shows every cue by name. Use the up and down arrow keys to move between them — as you land on each cue, RemSound plays its current sound, so you can hear what's set just by arrowing through. A cue that is switched off stays silent. The **Choose sound** list and the **Play** and **Browse** buttons all act on the cue you are on in this list.

 ### Turning a cue on or off, and choosing its sound

 The **Choose sound (Alt+D)** list, just below the cue list, sets the sound for whichever cue is highlighted in the Audio cue sounds list. Its first entry is **(none)** , which switches that cue off. Next comes **Your own file** , if you have picked one with Browse, then the built-in sounds that cue ships with. Arrow through it and RemSound plays each sound as you land on it and makes it that cue’s sound.

   * Land on **(none)** and the cue is switched **off** — nothing plays for that event.
   * Land on a sound and the cue is switched **on** and set to use that sound.
   * If you have picked a file of your own for the cue with Browse, it is listed as **Your own file** , straight after (none), and is selected while it is what plays. Land on a built-in sound and the cue switches to that sound instead. Land back on **Your own file** before you close Preferences and your file is back.



 Every cue starts switched on, using its first sound. **"(none)" is how you silence a cue** — there are no separate tickboxes any more. Your choices are remembered for next time.

 ### Which cue settings travel with the profile

 Eight cues are **saved with the profile** : connect, disconnect, recording start and stop, profile saved, profile switched, profile menu open and update. For those eight, whether the cue is on, and any file of your own you've picked for it, go with the profile. So a “quiet listening” profile might have them all off, while a “live monitoring” profile keeps them on. Save the profile (Ctrl+S) to keep the change.

 The other cues — startup, send and receive turned on and off, minimise, restore, checkbox ticked and unticked, and switch tabs — are kept on this computer and apply to every profile. Which of a cue's built-in sounds is chosen is also kept on this computer, for every cue. Your own sound files stay where you picked them on your disk; RemSound just remembers where they are.

 ### Previewing and choosing a different sound

 Below the list are two buttons that act on whichever cue is currently highlighted in the list:

   * **Play [cue name] (Alt+P)** — previews the sound of the cue highlighted in the Audio cue sounds list, through your default Windows output, so you can hear it without having to trigger the event. The button’s name follows the cue, for example _Play connect sound_. Works whether the cue is switched on or set to (none), so you can listen before deciding.
   * **Browse for [cue name] … (Alt+B)** — opens a Windows file picker so you can choose your own WAV file for the cue highlighted in the Audio cue sounds list, in place of its built-in sound. Once picked, the button’s label changes to “(custom)” to remind you the cue is using your file, and your sound plays the next time the event fires. If the cue was switched off, set to (none), choosing a file switches it on: choosing a sound means you want to hear it. To go back to the built-in sound, open this button’s context menu (Shift+F10 or the Applications key) and choose **Use default [cue name]**.



 Both buttons' labels update as you arrow through the list, so you always know which cue you're about to act on.

 ### If a cue's sound file goes missing

 If a cue is switched on but RemSound can't find its sound file — for example you delete a WAV you'd browsed for — RemSound brings its window to the front (even when it's minimised) and tells you which cue and which event it was, then switches that cue to **(none)** so it stops trying. Pick a sound for it again in the **Choose sound** list, or Browse for a new file, to turn it back on.

 ### Keyboard clicks while typing

 Just below the cue controls is a tickbox, **Play keyboard clicks when typing into any edit field (Alt+K)** , which is on by default. With it ticked, typing into any text box anywhere in RemSound plays a soft click on each key, so you get an audible sense of your typing, and password boxes add a second sound so you can tell them apart. It only sounds while your cursor is actually in an edit field — move out of the field and it stops. Untick it to switch all of this off; the setting applies to every profile on this computer.

 **Password boxes are treated specially:** they play the same key click _and_ a second, distinct sound at the same instant, so you can tell by ear that you're typing into a password box. That second sound only happens in password boxes. RemSound's own password boxes show what you type rather than dots, so your screen reader reads it back.

 ### Going back to the default sound

 To revert a cue to its default sound, **right-click** the _Browse for [cue name] …_ button and pick **Use default sound**. The custom path is forgotten and the cue goes back to playing the default WAV that ships with RemSound. The right-click option is greyed out when the cue is already using its default. (Alternatively, click _Browse_ and pick a file from RemSound's own `default sounds` folder — alongside the program — and RemSound treats that as “use default” and clears the override automatically.)

 ### Where the cue sounds live

 RemSound keeps the built-in cue WAV files in a `default sounds` folder alongside the program. Each cue ships with a small set of numbered sound files there. They're named after the cue with a number on the end — for example `connect 1.wav` and `connect 2.wav` for the connect cue, `record start 1.wav` and `record start 2.wav` for the recording-start cue, and so on. The **Choose sound** list described above picks between the numbered files a cue has.

 Because these are RemSound's own built-in sounds, an update can refresh them — if a future version ships an improved default, you'll get it. To use a sound of your _own_ for a cue, don't drop a file into `default sounds` (an update would overwrite it); instead use the **Browse** button, which links the cue to your file wherever you keep it. That link is remembered and never touched by an update, so your chosen sound always stays put.

> **Tip for sound designers:** the defaults are deliberately short and simple so they stay out of the way. If you'd like the cues to feel more in-character with a particular profile, the custom-sound feature is designed for exactly that. Keep WAV files short (well under a second usually works best) so cues don't overlap with each other on a busy day.

 ## 20\. Updating RemSound

 RemSound can check for a newer version on a schedule you choose, prompt you to install it, and either ask first or do it quietly. There's also a one-press “check now” button so you don't have to wait for the timer.

 ### Settings in Preferences

 Open **Options → Preferences** (or Ctrl+P) and go to the **Update settings** tab:

 Setting| Shortcut| What it does
 ---|---|---
 **Check for updates on startup** (checkbox)| Alt+S| When ticked, RemSound has a quiet look for a newer version a few seconds after each launch. Switching profile doesn't count as a launch, so it won't ask again then. On by default. Combined with _Silently install updates when available_ , this means leaving RemSound to keep itself up to date without you ever needing to press anything. Untick if you'd rather only ever check on a timer or by pressing _Check for updates now_.
 **Then check every** (drop-down)| Alt+U| How often RemSound checks for a newer version in the background _after_ launch. Choices: _Never_ , _Every hour_ , _Every 6 hours_ , _Every 24 hours_. The default is _Every 24 hours_. Your choice is remembered between launches; if you set it to _Never_ and you've also unticked the startup check, the only way an update arrives is through _Check for updates now_ , or Check for updates on the Help menu.
 **Check for updates now** (button)| Alt+N| Checks for a newer version straight away. If you're already up to date you get a small popup saying so. If there's a newer version, you get a confirmation dialog with the release notes and a Yes / No to install. The same button is in the Help menu (Alt+H, C).
 **Silently install updates when available** (checkbox)| Alt+I| When ticked, the background and startup checks install any available update without asking — RemSound downloads it, closes briefly, swaps the files, and reopens itself. Off by default. The startup check shows a brief notice first so you can see what's about to happen. The _manual_ “Check for updates now” button always asks first, no matter how this checkbox is set.
 **Only install updates within this time range** (checkbox)| Alt+T| When ticked, _automatic_ installs (the startup check and the background timer) only go ahead inside the daily time range set by the _Start time_ and _End time_ lists under it — so an update never closes RemSound and kills your sound while you're mid-session. If an update is found outside the range, RemSound notes it and quietly retries the moment the range opens. Off by default. The manual “Check for updates now” button ignores the range — if you ask by hand, you get it straight away.
 **Start time** (drop-down)| Alt+A| The time of day from which automatic updates may install, in 15-minute steps from 00:00 to 23:45; the default is 01:00, and the start minute counts as inside the range. It is only available while _Only install updates within this time range_ is ticked. If the end time is at or before the start time, the range runs past midnight: 22:00 to 06:00 covers late evening straight through to early morning.
 **End time** (drop-down)| Alt+D| The time of day at which the range for automatic updates closes, in 15-minute steps from 00:00 to 23:45; the default is 06:00, and the end minute is outside the range. It is only available while _Only install updates within this time range_ is ticked. An end time at or before the start time runs past midnight: 22:00 to 06:00 covers late evening straight through to early morning.
 **Show what's new after each update** (checkbox)| Alt+H| When ticked, the first time RemSound opens after an update has installed, it pops up the About box — which starts with the notes for the version you just got — so you can see what changed. On by default. It only happens once per update, never on an ordinary restart, and never on a fresh install.

 ### The brief notice before a silent update installs

 If RemSound finds an update right after launch and is set to install silently, it now shows a small window so you're not surprised when the app closes a few seconds in. The window says “RemSound vX.X is ready to install” with three buttons:

   * **Install now** (Alt+I) — downloads and installs the update straight away: RemSound closes, the new version is put in place, and RemSound opens again on the same profile. This is the default: Enter picks it, and so does leaving the notice alone for eight seconds.
   * **Skip this version** (Alt+S) — closes the notice without installing the update. RemSound doesn't remember that you skipped it, so its next check for updates finds the same version again: a check in the background may install it, and the check when RemSound next starts shows this notice again. To stop updates installing on their own, untick **Silently install updates when available** in Preferences.
   * **Postpone** (Alt+P, or Escape) — closes the notice without installing the update now. RemSound's next check for updates finds it again: a check in the background may install it, and the check when RemSound next starts shows this notice again.



 A short countdown picks _Install now_ automatically if you don't choose anything — long enough to read the version number, short enough that walking away from your desk doesn't block the silent update. Esc has the same effect as Postpone. The countdown waits while context help is open, so you can press F1 on a button and read about it without the update starting.

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

 The update download is best-effort: a flaky network, or a temporarily-unavailable version, will pop up a message saying it couldn't finish, and leave your running version untouched. If the download stops arriving altogether, RemSound gives up after a minute with nothing coming in and tells you, so you can try again straight away; a download that is just slow is never cut off. The address of the download page is in that message, so you can get the new version in a browser and install it by hand if you need to. If you installed RemSound into `Program Files` without giving your account permission to write to that folder, the install can't replace the files there — either fix the permission or move RemSound to a folder you can write to (somewhere inside your own user folder, for instance).

 **If the file swap itself can't finish** — for example because a sync app or another program was holding one of the files open and wouldn't let go — RemSound _puts your previous version back exactly as it was_ , trying each file for up to a minute, and reopens it, rather than leaving you with a half-finished install. When it opens, it tells you once that the update didn't go through and that nothing is broken — just try **Help → Check for updates** again, and it almost always goes through on the next attempt. If a sync app like Dropbox is involved, closing RemSound and giving it half a minute to settle before retrying helps.

 **In the rare case that it can't put a file back either** , RemSound doesn't pretend otherwise. It keeps the old files it couldn't restore in a folder next to the program called `_update-backup-kept-` followed by the date and time, and every time RemSound starts it tells you which files aren't right and where the old ones are, until an update goes through. The message also asks whether you've put it right. No is the default, and then it tells you again next time. Answer Yes once you have put it right, and it stops. It also stops by itself once every part of RemSound is the same version again — after a good update, or after you unzip RemSound over the folder — and the old files kept aside are then removed.

 **If an update is cut off part way** — Windows shuts down in the middle of it, say — RemSound notices the next time it starts. It keeps the old files aside and tells you with the same message, and the lock-screen service keeps the version it has until the install is whole again. The easiest fix is **Help → Check for updates** again, or downloading RemSound and unzipping it over the folder. Until then, the lock-screen service carries on with the version it already has rather than copying a half-finished one. The same message is also saved as `update-incomplete.txt` next to the program, in case RemSound itself won't open.

 A step-by-step record of every update is kept in a file called `updater.log` in the install folder — useful if a failure keeps happening and you want to share it for diagnosis.

 ### The About dialog and release notes

 To see which version you're on without checking for updates, open **Help → About RemSound** (Alt+H, A). The dialog shows the version number and the release notes for the newest five versions, in a scrollable read-only box — newest first, so what just changed is right at the top. Notes for every older version live on the RemSound releases page on GitHub. Close (or Esc) dismisses it.

 Control| Shortcut| What it does
 ---|---|---
 **Release notes**|  —| A read-only box in the About dialog with the release notes for the newest five versions of RemSound, newest first. Tab into it and arrow through it to read. Notes for older versions are on the RemSound releases page on GitHub.
 **Close**|  Alt+C, Enter or Escape| Closes the About dialog.

 ## 21\. Recording to a file

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
 **MP3**|  A compressed file at one of four bitrates: 128 / 192 / 256 / 320 kbps (320 is the default). Stereo or mono. MP3 plays just about everywhere.| Long sessions where file size matters; quickly sending someone a listen-once file.
 **OGG-Opus**|  A compressed file using Opus, at one of four target bitrates: 96 / 128 / 192 / 256 kbps (192 is the default). Stereo or mono. The file extension is `.opus`.| Smaller files than MP3 at similar quality; plays in most modern players (VLC, mpv, web browsers).
 **FLAC**|  A compressed file with no quality loss at all. Bit-depth choices: 16-bit or 24-bit (the default). Stereo or mono. Files are typically about half the size of the same recording as WAV, with no loss of quality.| Keeping a master copy when you also want a sensible file size — it plays back identically to WAV but is half the size.

 **Surviving a crash.** All four formats are designed to leave a playable file behind even if RemSound crashes partway through a recording. You lose at most about 5 seconds of recently-captured sound on a crash, never the whole session.

 ### Start and stop sound cues

 RemSound plays a short ding when a recording starts and another when it stops, so you have an audible confirmation that the toggle actually took effect. These are two of the cues described in Audio cue sounds. You can turn either or both off, replace them with your own WAV files, and preview them from Preferences. The built-in defaults are the `record start` and `record stop` sounds in the **default sounds** folder alongside the program.

 ### Where recordings go

 Unless you choose another folder, recordings go in a folder called **recordings** , inside the RemSound folder.

 Inside your recordings folder, RemSound keeps a folder for each **date** — for example `2026-07-04` — so your recordings sort neatly in order. Everything from a given day goes into that day's folder.

 All the times below are written 24-hour, as hours-minutes-seconds (`HH-MM-SS`), so files and folders line up in time order in Explorer.

   * A normal (non-split) recording is a single file named like `14-30-05 RemSound recording DESKTOP-ABC.wav` — the time, then “RemSound recording”, then this computer's name (and the right extension for your chosen format).
   * A **split** recording (when you tick “Split recording into separate tracks”) is instead a _folder_ named like `14-30-05 RemSound recording multi track`. Inside it there's one file per peer, named `<peer name> 14-30-05.wav` (the friendly name you've given the peer if it has one, otherwise its computer name, or its address if it has neither), plus your own send as `<your computer name> 14-30-05.wav`.



 You can change the folder via **Record → Change recordings folder**. Choosing a different folder saves that location into your current profile, so it travels with the rest of your settings — switching profiles can switch your recording destination too. If a profile points at a folder that doesn't exist on this computer, RemSound creates it when you start recording. If it can't (a drive that isn't there, say), you get a message and nothing records.

 **Record → Open current recordings folder** opens Windows File Explorer on whatever folder is currently set, creating it on the spot if no recording has been made there yet.

 ### Starting and stopping

 There are three ways to start or stop a recording, and closing RemSound stops one for you:

   * **Ctrl+R** from anywhere in the main window — a toggle. The menu item text switches between “Start recording” and “Stop recording” to show the current state.
   * **Record → Start recording** (or Stop, when one is in progress).
   * The **Start / Stop recording** global hotkey — works system-wide, even when RemSound is minimised or isn't the active window. It's unset by default; set a combination in the Keyboard shortcuts dialog (Ctrl+K). See Global hotkeys.
   * Closing RemSound while a recording is running finishes the file cleanly — you don't lose anything if you forget to stop it manually.



 Recording happens in the background, so it doesn't affect the sound or the network. The recorder holds about five seconds of sound waiting for the disk. If the disk falls further behind than that, new sound is dropped and noted in the log, and once a full second has been lost RemSound warns you; the recording carries on. If the disk fails outright (full, or unplugged), the recording stops and RemSound tells you, naming the partly-written file. In practice you'll only meet this on a very slow USB stick or network drive.

 ### Recording settings dialog

 Reached via **Options → Recording settings** (Alt+O, S). Two tickboxes sit at the top, then up to five keyboard-navigable lists laid out left to right. FLAC's compression level has its own list, but it only shows when the file format is FLAC.

 Control| What it does
 ---|---
 **Split recording into separate tracks** (tickbox, Alt+T)| Off by default. Ticked, instead of one mixed file, a recording becomes a _folder_ with one file per connected peer — each holding only that peer's sound — plus one file for your own send. Which of those files you get follows the **Recording source** list in this dialog: _Record all received_ gives you the peer files; _Record both sent and received_ gives the peer files plus your own; _Record all sent_ gives just your own. People who connect after the recording has started don't get a file of their own. With _Record all received_ chosen, a split recording won't start while nobody is connected, and RemSound tells you why.
 **Bypass pan and EQ when recording** (tickbox, Alt+R)| Records the raw sound — before any volume, pan or EQ you've set on the Volume, pan and EQ for peers tab — even though you still hear the shaped version. Left off (the default), the recording captures what you actually hear, including your shaping; and on a split recording each peer's own file carries that peer's own shaping.
 List| Shortcut| What goes in it
 ---|---|---
 **Recording source**|  Alt+S| What goes into the recording. _Record both sent and received audio_ (the default) mixes both directions into one file. _Record all received audio_ records everything coming in from your peers, and _Record all sent audio_ records what you are sending out.
 **File format**|  Alt+F| WAV (the default), MP3, Ogg-Opus or FLAC. Changing it changes the choices in the **Audio format attributes** list, and the **FLAC compression level** list appears only when FLAC is chosen. Every format records at 48 kHz.
 **Audio format attributes**|  Alt+A| The quality for the chosen file format, with the 48 kHz sample rate stated on every row. WAV: 16-bit, 24-bit (the default) or 32-bit float. MP3: 128, 192, 256 or 320 kbps (the default). Ogg-Opus: 96, 128, 192 (the default) or 256 kbps. FLAC: 16-bit or 24-bit (the default).
 **FLAC compression level**|  Alt+L| Shown **only when FLAC is the chosen file format**. Nine rows, levels 0 to 8: 0 is the fastest to encode and makes the biggest file, 8 is the slowest and makes the smallest, and 5 is the default. Every level produces an identical, no-loss recording — it's purely a trade-off between encoding speed and file size.
 **Channels**|  Alt+C| Stereo (the default) or Mono, which mixes left and right into one channel. Applies to every format.

 OK (Alt+O) saves your choices to the current profile. Cancel (Alt+N) or Esc discards them. Settings are saved with the profile as usual — changes here mark the profile as having unsaved changes, and you'll be asked about them on exit if you haven't saved.

 Control| Shortcut| What it does
 ---|---|---
 **OK**|  Alt+O, or Enter| Closes Recording settings and keeps your choices for the current profile. The next recording you start uses them. If you changed anything, the profile is marked as having unsaved changes; save the profile to keep them for next time.
 **Cancel**|  Alt+N, or Escape| Closes Recording settings without changing anything. Every choice made since you opened it is thrown away.

 ## 22\. Logs and diagnostics

 Everything to do with logging lives on its own **Logging** tab in the Preferences dialog (Options → Preferences, or Ctrl+P). If logging is turned on (the **Enable logs** checkbox there, off by default), RemSound writes a log file each session into a `logs` folder inside **user settings and logs** — the same folder your settings and profiles live in. One file per launch.

 The file contains two kinds of rows:

 Kind| Contents
 ---|---
 EVT| Event lines — startup, a peer being selected, capture starting, errors, and so on.
 SNAP| One-second snapshots of running figures: codec, jitter buffer, how much sound is buffered, packets sent, packets received, drop-outs, drops, and peer round-trip times.

 The **Write logs now** button on the Logging tab (Alt+W within the dialog) writes a “user requested write logs now” marker into the log, so you can find that moment in the file afterwards. It only writes anything while **Enable logs** is ticked.

 ### The Logging tab’s settings

 Control| Shortcut| What it does
 ---|---|---
 **Enable logs**|  Alt+L| When ticked, RemSound writes a log file for each session into the `logs` folder inside **user settings and logs** , and the DAW plugin writes its own log there too. Off by default. The change takes effect straight away, is kept on this computer, and applies to every profile.
 **Warn at startup if the logs folder is larger than**|  Alt+S| When ticked, each time RemSound starts it checks the size of the logs folder and shows a notice if it is bigger than the number of megabytes in the box after this one. It only warns; it never deletes anything. Off by default.
 **Warn when the logs folder is larger than this many megabytes**|  Alt+M| The size, in megabytes, above which RemSound warns you at startup that the logs folder is getting large. Type a number from 1 to 100,000, or use the up and down arrows, which move in steps of 10; the default is 100. It is only available while _Warn at startup if the logs folder is larger than_ is ticked.
 **Delete logs older than**|  Alt+D| When ticked, each time RemSound starts it quietly deletes any log file older than the number of days in the box after this one. The log it is writing right now is never touched. Off by default, so nothing is deleted unless you tick it.
 **Delete logs older than this many days**|  Alt+Y| How many days old a log file must be before RemSound deletes it at startup: from 1 to 30, and 14 by default. It is only available while _Delete logs older than_ is ticked.

 ### The DAW plugin writes its own log too

 The plugin runs inside your music software rather than inside RemSound, so it cannot share the app's file. It writes its own, into the same `logs` folder, named `RemSoundPlugin-` and then your computer's name. The **same** Enable logs checkbox controls it — with logging off, no plugin log is written at all.

 You get one file per plugin instance, so two tracks each running RemSound produce two files. Between them the app's log and the plugin's cover both halves of the conversation, which is why sending both is worth doing if something needs looking at.

 The plugin's file has the same two kinds of rows. Its snapshot line records what that track was doing, whether RemSound was answering, what your music software was set to, whether RemSound had to convert the sound to match it, whether any sound arrived late, and how much sound was waiting to be played. If a track ever sounds wrong, that line is usually where the reason is.

 You can turn logging on while your music software is already open — the plugin notices within a second and starts a file, so you do not have to reload anything to catch a fault that has already begun.

 ### Keeping the logs folder tidy

 Log files are small, but if you leave logging on for months they add up. The **Logging** tab has three ways to keep the folder under control, all switched off to begin with so nothing is ever deleted unless you ask for it:

   * **Warn at startup if the logs folder is larger than … megabytes** (Alt+S) — tick this and pick a size, and the next time RemSound starts it checks the folder and pops up a friendly notice if it has grown past that size. It only warns; it never deletes anything itself. The size box (Alt+M) starts at 100 megabytes and stays greyed out until you tick the box.
   * **Delete logs older than … days old** (Alt+D) — tick this and pick a number of days, from 1 to 30, and each time RemSound starts it quietly clears out any log older than that. The log it's writing right now is never touched. The days box (Alt+Y) starts at 14 and stays greyed out until you tick the box.
   * **Delete all logs** (Alt+A) — a button that permanently deletes every log file in the logs folder in one go. It asks you to confirm first, Yes or No (No is the default), then tells you how many it removed. The log RemSound is writing right now is kept; everything else goes, and it cannot be undone.



 Logs are plain text and can be opened in any text editor, or in a spreadsheet. The most useful figures when something feels wrong:

   * **BufferMs** — how much sound is queued up ready to play. It should sit close to your jitter buffer setting.
   * **Underruns** — how many times the playback reserve ran dry. Each one is a tiny click. A few per minute is normal over the internet; hundreds per second means something is broken.
   * **Drops** — packets thrown away because the reserve overflowed. This should stay near zero in normal use.
   * **Heartbeat** — the round-trip time to each connected peer. `pending` / `unreachable` / `stale` mean there's a problem.
   * **OpusFecRecoveries** — a running total of single missing packets that Opus quietly repaired. A number that's growing means Opus is saving you from clicks. Only meaningful when an Opus codec is in use.
   * **OpusUnrecoveredGaps** — a running total of multi-packet losses that Opus couldn't repair. Each one is an audible click. It stays at 0 on a clean connection; small numbers are normal over the internet.



 ## 23\. Command-line options

 RemSound is normally a windowed program you click to open. But it can also take **command-line options** — short text instructions you type after the program name. They are handy for three things: checking a machine quickly (what devices are present, does the audio path work at all), getting a support report to send to whoever helps you, and starting RemSound a particular way from a shortcut or a script.

 To use them, open a command prompt (press the Windows key, type `cmd`, press Enter), then run RemSound with the option after it. If RemSound is on your desktop you can type the whole path in quotes, for example:


     "C:\Users\you\Desktop\RemSound\RemSound.exe" --devices


 The options that just report something print their answer straight into the same command window as plain text — a screen reader reads it normally — and then RemSound exits without opening a window. The start-up options open RemSound as usual, just set up the way you asked.

 ### Options that print something and then exit

 Option| What it does
 ---|---
 `--help` or `-h`| Lists every option, the same as this section in short form.
 `--version`| Prints which version of RemSound this is: the word RemSound followed by the version number.
 `--devices`| Lists every microphone and line-in, every speaker and headphone output, and every ASIO driver on the machine — each with its sample rate, channel count and the exact device id RemSound uses internally. This is the quickest way to confirm an interface is actually present and seen by Windows.
 `--list-profiles`| Lists the names of your saved profiles. It only reads them and changes nothing.
 `--list-named-peers`| Lists the friendly names you've given peers, with each one's machine name and where and when it was last seen. It only reads them and changes nothing.
 `--selftest`
(or `--smoke-test`)| Runs RemSound's built-in self-test and reports **PASS** or **FAIL**. It works through a list of named checks. It passes a test sound through RemSound's whole path on this computer alone — picking it up, packing it, sending it to itself, receiving it and unpacking it — once with PCM and once with Opus. It also checks the encryption, the format of the messages sent over the network, saving and loading settings and profiles, that a support report never contains a password, and that the built-in sounds and this manual are present. Nothing is played out loud, so it is safe to run silently. Add `--seconds N` to make the sound part run for longer than the default.
 `--perftest`
[`--seconds N`]| Runs the audio path through several short cycles and checks that RemSound doesn't keep using more and more of the computer's memory and resources as it runs. Prints the numbers each cycle so two versions can be compared. Nothing is played out loud.
 `--diagnostics`| Writes a single plain-text report file holding the version, the operating system, the current settings, the list of profiles, the full device list, a check of the Windows microphone-privacy permission, a quick live audio self-check, the most recent session snapshot and recent warnings from the log, and the tail of the most recent log. With no path it saves into the **user settings and logs** folder and prints where it put it; you can also give a path, for example `--diagnostics C:\Users\you\Desktop\report.txt`. This is the file to send when asking for help — it answers most questions in one go.

 ### Options that change a setting or control a running copy, then exit

 Option| What it does
 ---|---
 `--log on` or `--log off`| Turns the diagnostic log on or off. The change takes effect the next time RemSound starts. The same setting lives in the Preferences dialog; this is just a way to set it without opening the window.
 `--close`| Closes a copy of RemSound that is already running. It closes it the normal way, finishing any recording properly, and only forces it closed if it is still running after 20 seconds. Useful in a script that needs to restart it.
 `--install-plugin` [folder] and `--uninstall-plugin`| Install or remove the DAW plugin, exactly as **DAW plugin → Install plugin** and **Remove plugin** do. Give `--install-plugin` a folder and the plugin goes into a `RemSound` folder inside it, remembered for updates; without one, it goes where it is now, which is the standard place unless you chose another. The folder must be a whole path, such as `C:\Program Files\Common Files\VST3`. The scripts in the **Install and Uninstall Scripts** folder use these.
 `--service install`, `uninstall`, `start`, `stop` or `status`| Do what the **Service** menu does — Windows asks for administrator permission in the same way — or, with `status`, say whether the service is installed and running. Nothing is asked first: the scripts in the **Install and Uninstall Scripts** folder ask the menu's questions before they call it.
 `--control "<command>"`| Sends a command to a copy started with `--headless` and prints its answer. Give `--control` several times to send several commands in a row. See Running RemSound with no window below.
 `--uninstall`| Uninstalls an installed copy of RemSound — the same as **Options → Uninstall RemSound from this PC**. It asks you to confirm first. On a portable copy it just tells you there is nothing to remove.

 ### Options that change how RemSound starts

 These open RemSound as normal, set up the way you ask, and are meant for shortcuts and scripts.

 Option| What it does
 ---|---
 `--profile "<name>"`| Starts straight into the named profile and skips the profile picker. Put the name in quotes if it contains a space, for example `--profile "Studio link"`.
 `--connect <ip>`| Starts and connects to a peer at that address. You can give just an address (`--connect 192.168.1.42`) or an address and port (`--connect 192.168.1.42:47830`); with no port it uses RemSound's normal port, 47830. If you don't also give a `--profile`, it starts on a fresh blank profile already pointed at that peer.
 `--minimized`, `--minimised` or `--tray`| Starts minimized to the notification area, with no window popping up. Pair it with `--profile` or `--connect` so it has something to do without waiting at the picker.
 `--config-dir <folder>`| Uses an explicit folder for this run's settings, profiles and logs, instead of the usual location. (The built-in cue sounds still come from the **default sounds** folder next to the program.) It lets you (or an automated test) run RemSound against a throwaway folder without touching your real settings. Works with any command — for example `--selftest --config-dir C:\Temp\rstest` or `--diagnostics --config-dir C:\Temp\rstest`.
 `--silent`| Plays no cue sounds, and shows none of the messages or questions RemSound raises by itself — someone asking to connect, a password that doesn't match, a missing sound file, the notices at start-up. They are written to the log instead. Meant for automated or unattended launches.
 `--headless`| Runs RemSound with no window at all, no cue sounds and nothing spoken by your screen reader, driven by `--control` instead. See Running RemSound with no window below.

 ### Running RemSound with no window

 Started with `--headless`, RemSound does everything it normally does — your profile, your sound cards, your peers, your server — but no window of it ever appears or takes the focus, it plays no cue sounds, and your screen reader hears nothing from it. It is driven from a command prompt or a script with `RemSound.exe --control "<command>"`, the way a script can drive a DAW. It is meant for testing and automation, and for anyone who wants to set RemSound up from a script.

 The commands reach every control, menu and dialog there is, by the name your screen reader reads for it. `--control help` lists them. The main ones:

   * `windows` — what is open, including any question waiting for an answer.
   * `list` — every control in the window with its state; `get <control>` shows one in full, with a list's items and ticks.
   * `set <control> <value>` — for example `set Send my audio on`, or a choice, a number or some text.
   * `check <list> <item> on` or `off` — tick or untick someone in a peer list, or a device.
   * `click <button>`, and `menu File > Save` for a menu item.
   * `answer <button>` — answers the dialog or question that is waiting, for example `answer No`.
   * `status` — the connection status and health lines. `quit` closes RemSound.
   * `key <control> <keys>` — presses keys in a control, for example `key Connected peers space`. A button's Alt key presses the button. The Alt key of a tick box or a box you type in can't work here, because a window nobody can see can't take the keyboard; use `set` or `click` for those.
   * `wait <words> [seconds]` — waits until a window or question with those words is open, or a line with them is written to the log. `wait gone <words>` waits until it has closed. Ten seconds unless you say otherwise.
   * `log`, `cues` and `speech` — what RemSound has just written to its log, the sounds it would have played, and what it would have said to your screen reader. Add a number for more lines, and words to see only lines with them in. The log works even with the log file turned off.
   * `settings` — this computer's RemSound settings and the profile as it stands, with every password hidden.



 A window you open yourself, or a question that follows from something you did, such as whether to save your changes, still waits out of sight until it is answered with `answer`. While it waits, nothing else can be used, just as when a dialog is open in front of you. Messages and questions RemSound raises by itself are not shown in a headless run; they are only written to the log. That includes someone asking to connect to you, the offer to treat a typed address as a server, a password that doesn't match, and the notices at start-up. If you want the window after all, choose **Show RemSound** from its notification-area icon: from then on it behaves normally — its windows, its sounds (unless it was started with `--silent`) and what your screen reader hears — and `--control` keeps working.

 Only a copy started with `--headless` listens for commands, and only your own Windows account on this PC can send them — never another account, and never anything over the network. Passwords are never read back. Every command is written to the log, with what it did.

 ### Examples


     RemSound.exe --devices
     RemSound.exe --list-profiles
     RemSound.exe --selftest
     RemSound.exe --diagnostics
     RemSound.exe --profile "Studio" --minimized
     RemSound.exe --connect 192.168.1.42
     RemSound.exe --headless --profile "Studio"
     RemSound.exe --control "check Connected peers iPhone on" --control status


 A common support sequence: ask the person to run `--diagnostics` and send you the file, then have them run `--selftest` — if that says PASS, capture, encoding and the audio path are all sound on their machine and the problem is somewhere in the connection between you.

 ## 24\. The lock-screen service (send only)

 Normally RemSound runs as an app you open and close. The **lock-screen service** is a second, silent way to run it: a Windows service that keeps _sending_ this machine's audio to your peers even when the screen is locked, when you have signed out, and when nobody is logged in at all (for example after an unattended restart). It is useful when a machine needs to stream its audio continuously without someone sitting at it.

 It is deliberately limited:

   * **Send only.** The service captures and sends; it never plays received audio. (Windows silences audio output when no one is logged in, so receiving on the lock screen is not possible.) Use the normal app for listening.
   * **WASAPI only.** ASIO devices cannot be used by a Windows service. If you need ASIO, use the normal app.
   * **It gets out of your way.** Whenever you open the normal RemSound app, the service automatically stops sending and hands over to you — so you are always in control while you are at the machine. When you close the app (or it crashes, or you sign out), the service takes over again a couple of seconds later, re-reading its profile so any changes you made are picked up. On a server the service is the same person as your own RemSound, as long as you saved its profile from your Windows account, so anybody who had ticked you doesn’t have to tick you again when it takes over. You never need to stop it by hand to make a change.



 ### Setting it up

 Everything lives in the **Service** menu on the menu bar:

   1. **Configure service profile …** — opens a window with two tabs: **Connectivity** (who to send to, the password, and the server) and **Audio send** (what to send). This is a profile of its own and doesn't appear in your usual profile list. See what's in it.
   2. **Install service** — registers it with Windows so it starts automatically at every boot. Windows asks for administrator permission (one prompt). Do this once. Straight after installing, RemSound asks whether you'd like to **start it now** (otherwise it waits until the next reboot). (When you first install RemSound on a PC, the app installer also offers to set the service up — and start it — for you, so you may have done this already.)
   3. **Start service** / **Stop service** — run or halt it now without waiting for a reboot.
   4. **Uninstall service** — removes it entirely.
   5. **Repair service folder access** — fixes the permissions on the service's settings folder so your account can save the service profile and read the service logs again. You should never need this in normal use, but if saving the service profile ever fails with an “access denied” message, or the service's log files won't open, run this once (Windows asks for administrator permission) and everything is put right. RemSound also checks the folder itself every time it starts and offers this same repair automatically if it finds a problem. It offers it only to the Windows account the service belongs to: another account on the same computer is told once that the service was set up from another account, and left alone.
   6. **View service log** and **View service update log** — open the service's own log, and the record of it updating itself. See Service menu.



 **If you can't reach RemSound's window,** the same four are in the **Install and Uninstall Scripts** folder next to `RemSound.exe`: double-click **Install service** , **Uninstall service** , **Start service** or **Stop service**. Each asks what the menu asks (press 1 or 2 to answer) and does exactly what the menu item does, Windows' administrator prompt included.

 ### The service profile, in detail

 Setting| What it does
 ---|---
 **Peers to send to (Alt+C)**|  On the Connectivity tab. The computers the service sends to. Tick the ones it should send to; with none ticked, it sends to nobody. Press Delete on one to take it out of the list.
 **Add peer by IP (Alt+A)**|  On the Connectivity tab. Opens a box where you type an address or computer name; press Enter and it is added to the **Peers to send to** list, ticked. A peer already in that list isn't added twice.
 **Set service profile password … (Alt+W)**| On the Connectivity tab. Opens a box to see or change the password the service uses. Like any profile password, it must match the people it sends to. Beside the button, RemSound says “No password set.” or “Password set.”
 **Server address (Alt+V)**|  On the Connectivity tab. The server the service joins, every time it starts. Leave it empty and it never joins one. There is no Connect button: nobody is at a screen when the service runs, so the address is the instruction. The line under the box says what will happen, including whether the service can be reached by people on that server.
 **How to send WASAPI audio (Alt+1)**|  On the Audio send tab. _Send whole audio devices_ (the default) sends the devices ticked in **WASAPI audio outputs to send**. _Send specific applications_ sends only the applications ticked in **Applications to send** , and shows that list in place of the devices.
 **WASAPI audio outputs to send (Alt+2)**|  On the Audio send tab. Its first choice is **Use Windows default audio device, follows Windows changes** : it sends whatever this machine is playing, and keeps following the Windows default if that changes later. You can tick specific output devices instead.
 **Applications to send (Alt+3)**|  On the Audio send tab, shown when **How to send WASAPI audio** is set to _Send specific applications_. It lists the applications using sound on this PC now, plus any you ticked before that aren't running, marked “(not running)”. Tick the ones the service should send.
 No microphone list| The service can't send a microphone. It sends what this machine plays, or the applications you pick.
 No “send my audio” switch| The service always sends. There is no audio-quality tab either: it uses the settings that work best for live streaming (Opus live-latency, small packets, locked to the audio clock), so you do not have to choose them.
 **Additional options … (Alt+O) →** Enable service logging (Alt+L)| Off by default. Turns on the service's own log, which is separate from the app's. Read it with Service → View service log.
 **Additional options →** Accept people who tick this service on a server (Alt+A)| Off by default. With it off, the service reaches exactly the people its profile named when you saved it. Tick it and somebody who joins the server later can reach the service without you opening the app. That includes a phone or older app the server pairs with the service: it gets sound only when this is ticked.
 **Additional options →** Set the machine's volume when the service starts (Alt+V)| Off by default. Tick it, then pick a percent in **Volume percent** (Alt+P) and when to set it in **When** (Alt+W). The service unmutes the machine and sets that volume when it starts — useful for an unattended PC that booted muted, so it's audible with nobody at the keyboard.
 **When** (Alt+W)| Chooses when the service sets the machine's volume, and is only available while **Set the machine's volume when the service starts** is ticked. _Only the first start after each boot_ (the default) is the set-and-forget choice: it sets the volume once per boot and never touches it again, so it won't fight you while you're using the machine. _Every time the service starts_ re-applies on every start — but the service also restarts on its own for routine reasons (a RemSound update, saving the service profile), and this mode re-applies on those too. Either way, a re-apply is skipped if the volume was set within the last few minutes.

 Additional options has its own **OK** (Alt+O) and **Cancel** (Alt+C). Nothing in it is kept unless you then choose **Save and Close** (Alt+S) on the service profile window. That window's **Cancel** (Alt+N), or Escape, closes it without changing anything. Changes take effect from the service's next start; no reinstall needed. The service never plays cue sounds — it streams silently in the background — so there are no cue options here.

 Control| Shortcut| What it does
 ---|---|---
 **Additional options …**| Alt+O| Opens the Additional service options window: service logging, accepting people who tick the service on a server, and setting the machine's volume when the service starts. What you choose there is only kept if you then choose **Save and Close** in the service profile window.
 **Save and Close**|  Alt+S, or Enter| Saves the service profile, and any additional options you accepted, then closes the window. If the service is running, RemSound restarts it so the changes take effect straight away; otherwise they apply the next time it starts. From a Windows account other than the one that set the service up, nothing is saved, and RemSound says the service belongs to that account.
 **Cancel**|  Alt+N, or Escape| Closes the service profile window without saving anything, including any additional options you chose. The service carries on exactly as before.
 **Additional options →** Volume percent| Alt+P| The volume, from 0 to 100 percent, that the service sets this machine to when it starts; it also unmutes it. The default is 50. Only available while **Set the machine's volume when the service starts** is ticked.
 **Additional options →** OK| Alt+O, or Enter| Closes Additional service options and takes you back to the service profile window, holding your choices. They are only kept if you then choose **Save and Close** there; cancelling the service profile window throws them away.
 **Additional options →** Cancel| Alt+C, or Escape| Closes Additional service options without changing anything and takes you back to the service profile window.

 The top of the Service menu always shows the current state: not installed, installed and running, or installed and stopped.

 ### Good to know

   * The service sends to the exact peer addresses you list in its profile (a computer on your network, or a reachable address across the internet), and to the people it has ticked on its server, if its profile names one. It does not go looking for peers the way the normal app does.
   * If the network isn't ready when the service starts — at boot, say, or straight after waking from sleep — it can't find the computers named in its profile yet. It keeps trying every 30 seconds, and straight away when Windows says the network has come back, and starts sending to each one as soon as it's found. A name that never works doesn't interrupt the others.
   * While the service is running, this computer can show up under its computer name in other people's **Discovered peers not on a server** list. Ticking it there doesn't make the service send to them: it still sends only to the peers in its own profile, and to people on its server.
   * Because the service runs even when you are not logged in, it sends from the system account. If Windows Firewall ever prompts about RemSound, allow it so the audio can get out.
   * When RemSound updates itself, the service updates itself too, automatically — it notices the newer version, copies it in and restarts onto it on its own, with no prompt and nothing for you to do. (This happens on the service's own schedule shortly after the app updates; nothing breaks in the meantime.) If the app's own update didn't finish, the service keeps the version it has.
   * If the service's server is given by name and moves to a new address, the service looks the name up again after 30 seconds with no answer, and follows it there.
   * If two computers should each stream to the other unattended, install the service on both.



 ## 25\. The DAW plugin

 RemSound can also work _inside_ your music software. Put the plugin on a track and it can send that track to your peers, bring peers onto that track, or both at once. Peers who arrive on a track can be recorded, shaped and mixed like anything else, and you can have one person per track or several people on the same one.

 It is a VST3 plugin, so it works in Reaper, Cubase, Studio One and any other music software that loads VST3 plugins.

 ### Putting it on your machine

 Open the **DAW plugin** menu (Alt+G) and choose **Install plugin**. RemSound asks where to put it:

   * **Install in the standard place** puts it in a RemSound folder inside your own plugin folder, `C:\Users\your name\AppData\Local\Programs\Common\VST3`. Most music software looks there. Windows does not ask for an administrator password, and nothing outside your account is touched.
   * **Choose another folder...** opens Windows' folder picker, so you can put it anywhere. RemSound puts it in a `RemSound` folder inside the one you pick, so its files never mix with other plugins'.



 Restart your music software afterwards and RemSound appears in its list of effects.

 **If your music software doesn't list RemSound.** Some music software, Audacity for example, only looks for plugins in the shared plugin folder for everyone on the computer, and never looks in your own. Choose **Install plugin** (it says **Reinstall plugin** once the plugin is installed), then **Choose another folder...** , and pick `C:\Program Files\Common Files\VST3`. That folder is shared by everyone on the computer, so Windows asks for administrator permission. The copy in your own folder is removed, so your music software won't list RemSound twice.

 **Where updates go.** RemSound remembers the folder you chose, and whenever RemSound updates, it updates the plugin there too. After you install somewhere other than the standard place, a message says so, with a **Do not show me this message again** tick. RemSound only knows about that one folder: if you move or copy the plugin somewhere else yourself, that copy isn't updated, and you'll need to copy it across again after each update. If the plugin is in a folder that needs administrator permission, RemSound asks once after each update whether to update the plugin now, and Windows then asks for permission. Say **Not now** and you won't be asked again until the next update; **Reinstall plugin** updates it at any time.

 **Remove plugin** takes it away again, from whichever folder it is in. It removes only the files it put there, so other plugins in that folder are left alone. If the plugin is in a folder that needs administrator permission, Windows asks for it.

 **Context help sounds.** Inside your music software, the plugin plays the same context help sounds as RemSound: the ones set on the Audio cues tab of Preferences. RemSound puts a copy of each beside the plugin and keeps them in step when you change them. For a plugin in a folder that needs administrator permission, a change of sound reaches it at the next install or update.

 #### The “Install the DAW plugin” window

 Control| Shortcut| What it does
 ---|---|---
 **Where to install the plugin**|  —| A read-only message: where the standard place is, where the plugin is now if it's already installed, and that RemSound keeps it up to date wherever it goes. Shift+Tab into it from the buttons and arrow through it to read it.
 **Install in the standard place**|  Alt+S, or Enter| Installs the plugin in your own plugin folder, which most music software looks in, with no administrator permission needed. If it was in another folder, RemSound's copy there is removed. The keyboard starts on this button.
 **Choose another folder...**|  Alt+C| Opens Windows' folder picker. The plugin goes into a RemSound folder inside the folder you pick, and RemSound remembers it for updates. Pick `C:\Program Files\Common Files\VST3` for music software that only looks in the shared folder; Windows then asks for administrator permission. Cancel the picker and nothing is installed: you're back in this window.
 **Cancel**|  Alt+A, or Escape| Closes the window without installing anything.

 #### The “Plugin updates” message

 Control| Shortcut| What it does
 ---|---|---
 **Plugin updates notice**|  —| A read-only message shown after you install the plugin somewhere other than the standard place. RemSound will put plugin updates in that folder, but a copy you move or copy somewhere else yourself isn't updated, and will need copying across again. Tab into it and arrow through it to read it again.
 **Do not show me this message again**|  Alt+D| Tick it and this message isn't shown again when you install the plugin in a folder of your own, on this computer. Off by default. It takes effect when you close the message.
 **OK**|  Alt+O, Enter or Escape| Closes the message.

 Both are also in the **Install and Uninstall Scripts** folder next to `RemSound.exe`, as **Install DAW plugin** and **Uninstall DAW plugin** , for when you can't reach RemSound's window. **Install DAW plugin** asks where, like the menu: press 1 for the standard place, 2 for the shared folder for everyone on the computer, 3 to type in a folder of your own, or 4 to cancel. **Uninstall DAW plugin** removes it from whichever folder it is in. Each does exactly what the menu item does, Windows' administrator prompt included.

 ### How you use it

 Keep RemSound itself open. RemSound holds the connection to your peers; the plugin asks it for audio. Your password, your peers and your audio settings all stay in RemSound, so the plugin has little to set up.

 The plugin window has nine things, in this order:

 Control| Shortcut| What it does
 ---|---|---
 **Send this track to my peers**|  Alt+N| Sends what is on this track to everyone you are connected to in RemSound, as a stream of its own alongside anything RemSound itself is sending. It doesn't need **Send my audio** ticked in RemSound, and it never turns on your own microphone or other inputs: those go out only while **Send my audio** is ticked.
 **Receive peers onto this track**|  Alt+R| Brings the people ticked in the **Peers to receive** list onto this track. The track's own sound still passes through.
 **Receive all peers, including whoever joins later**|  Alt+J| Puts everybody you are connected to on this track, and anyone who connects later arrives on it by themselves. The track follows the session.
 **Peers to receive**|  Alt+P| Tick the people you want on this track. It is RemSound's own list of connected peers and keeps itself up to date, so somebody who connects while you are working turns up here without reopening anything. Somebody reached through a server reads “(on the server)”.
 **Send level in decibels**|  Alt+L| The level of what this track sends. 0 is unity, and it runs from −60 to +12. All the way down is silence, not just quiet.
 **Receive level in decibels**|  Alt+V| The level of what arrives on this track from your peers. 0 is unity, and it runs from −60 to +12. All the way down is silence, not just quiet.
 **Active**|  Alt+A| Untick it to switch both directions off without forgetting which ones you had on or who you were receiving; tick it again and the plugin carries on exactly as it was, even if you closed the window in between. Handy if your music software makes bypass awkward to reach from the keyboard.
 **Plugin status**|  Alt+S| A read-only box: what the plugin is actually doing, including whether RemSound is answering at all, and whether **Receive audio** is off in RemSound, in which case there's nobody to receive onto the track until you tick it there.
 **Context help**|  Alt+H| A button. It shows the help for every control in this window at once, for music software that keeps F1 for itself. Where F1 does reach the plugin, F1 on a control shows just that control's help.

 Sending and receiving both start switched off. A plugin you have just put on a track does nothing until you tell it to, so it can never start sending a track to people by surprise.

 The track's own sound always passes through. Receiving adds your peers on top of whatever is already on the track; it never silences it.

 A track that sends and receives at the same time sends only its own sound, never the people it is receiving, so nobody hears themselves come back.

 ### One person per track, or several

 For recording, one person per track is usually what you want at the mix: put the plugin on several tracks and tick a different person in each. For listening, tick several people on one track and they arrive mixed together — say, three people talking while they listen to your session. The same person can be on two tracks at once, and both get their full sound.

 Each person's own volume, pan and EQ are set in RemSound, on the Volume, pan and EQ for peers tab. The plugin's two levels are for the track as a whole.

 ### Why the plugin has its own levels

 In every music program the effects on a track come before its fader. So the fader changes what you hear from the track, but not one sample of what the plugin sends. Two tracks sending at full level can add up to more than full scale at the other end, and only the plugin's send level can bring them down.

 ### You never hear anyone twice

 When a track receives somebody, that person stops coming out of RemSound's own output. You hear them once, through your music software, where you want them. Let the track go — untick **Receive peers onto this track** , untick them in **Peers to receive** , untick **Active** , remove the plugin, or close your music software — and they come straight back to RemSound's output on their own. There is nothing to set.

 ### Saved with your project

 Each plugin remembers what it was doing, its two levels, and who it was receiving. People are remembered by their address, so a saved project finds them again the next time you open it. Somebody who is not connected when you open the project stays ticked, and arrives on the track the moment they connect again at the same address. Switching the plugin off with **Active** , or unticking **Receive** , keeps who you had chosen, even after you close the window or save the project, so switching back on puts everything back.

 ### Any buffer size

 The plugin works at whatever buffer size your DAW is set to, including large ones like 4096 or 8192, and at any sample rate. If your DAW changes its buffer size while you work, the sound carries on without a click.

 ### Pan and EQ

 If you have set volume, pan or EQ for a peer in RemSound, that shaping comes through to the track as well, so what you get is what you were hearing. If you would rather have the untouched signal and do the work in your DAW, untick **Apply pan and EQ to plugin audio** in the DAW plugin menu. It is the same choice recording already offers, and it costs nothing either way.

 ### If you don't use a DAW

 RemSound listens for plugins on your own machine only — nothing on the network can reach it. If you would still rather it listened for nothing at all, untick **Let plugins connect to RemSound** in the DAW plugin menu.

 ### If everything sounds about a third of a second late

 This one is not RemSound, and it catches everybody. Reaper renders each track’s effects up to 200 milliseconds ahead of where you are listening, and holds the result in a buffer. It is a big win for ordinary plugins and completely wrong for one carrying live audio off the network, because that audio then sits waiting in the buffer. Reaper has no way to know the difference.

 Two ways to switch it off, either is fine:

   * Record-arm the track with monitoring on. Reaper already skips the look-ahead for a track that is monitoring, so the delay collapses on its own.
   * Or turn it off for that track: Track performance options, then “Prevent anticipative FX processing”. There is also a global setting under Options, Preferences, Audio, Buffering.



 The same buffer explains a couple of other oddities: muting the track takes a noticeable moment to take effect, because the buffer has to drain first.

 ### If a track goes silent when you switch away from Reaper

 This is a Reaper setting, not RemSound. In Reaper's audio device preferences there is an option called “Close audio device when stopped and application is inactive”, and it is on by default. While it is on, the plugin stops receiving and sending as soon as you switch away from Reaper with playback stopped, because Reaper has closed its audio device. Untick that option.

 ### Good to know

   * Your music software can run at any sample rate. RemSound converts at the boundary, so nobody arrives at the wrong pitch.
   * Every control in the plugin window is a standard Windows control, so a screen reader reads it the same way it reads RemSound itself.
   * If your host makes the plugin window hard to reach, everything in it is also a plugin parameter, which every DAW lists and Reaper with OSARA reads out: Send this track to your peers, Receive a peer onto this track, Receive all peers, Send level, Receive level and Active. Choosing people works as a cursor and a tick: **Peer in list** moves along RemSound's list of connected peers, and **Receive the peer in list** says whether the person it is on is on this track. Moving the cursor never changes anything by itself.
   * Your music software cannot compensate for the network delay by itself, so a track recorded through the plugin sits a little late. Some plugins can tell your music software how late they are, so it can line things up. RemSound's plugin can't do that yet, so move recorded tracks back by hand.



 ## 26\. Troubleshooting

 ### I don't hear my friend

   1. Connectivity tab: does your friend's line in **Connected peers** say “connected” with a time in milliseconds? If not, check the **Connection status** box (Alt+S). “Pending” or “unreachable” there means the two computers can't reach each other.
   2. Audio inputs and outputs tab: is **Receive audio** ticked?
   3. Same tab: is at least one output device ticked, in the WASAPI or the ASIO output list?
   4. Same tab: is the volume slider above zero?



 ### My friend doesn't hear me

   1. Audio inputs and outputs tab: is **Send my audio** ticked?
   2. Same tab: is at least one capture source ticked across the three send lists?
   3. If you're using a microphone: is Windows allowing apps to use it? RemSound now pops up a warning when you switch on a microphone Windows is blocking — including when a profile loads with one already on — but to check by hand, open Settings → Privacy & security → Microphone and make sure both _Microphone access_ and _Let desktop apps access your microphone_ are on. (When Windows blocks it the mic sends silence rather than failing, so it's easy to miss.)
   4. Have they ticked _your_ name in their Discovered peers not on a server list?



 ### I can hear them but the sound crackles

   * **First, try raising Buffer smoothness** by 1 or 2 on the Audio profile tab (Alt+B). It usually fixes crackles for a smaller delay cost than raising the jitter buffer does.
   * Then, try raising the Audio jitter buffer value (Alt+L). Internet connections often need 30–80 ms.
   * If you're using PCM, switch to Opus — Opus can repair single missing packets automatically, which PCM can't. It's much more tolerant of an unsteady network.
   * If you're on Wi-Fi, try wired Ethernet — Wi-Fi adds 5–20 ms of unpredictable jitter.
   * If you'd set Packet size to Small and you're now on the internet rather than a same-house network, set it back to Standard. Small packets double the packet rate, which makes internet clicks more likely.



 ### The sound is fine but the delay feels long

   * Lower the relevant jitter-buffer value on the Audio profile tab, step by step, until clicks just start, then nudge it back up by one step. With an ASIO driver chosen, this is two separate controls (one for each path).
   * Or, turn on Continuous auto-tune for the path that feels slow and let RemSound find the right level.
   * Lower Buffer smoothness towards 3 if you'd been running it high “just in case”.
   * If only WASAPI matters for your session, set the ASIO driver picker to _(none)_ — that turns ASIO off entirely.



 ### The ASIO sound is grainy or constantly micro-clicks

 Most likely your ASIO jitter buffer is below the network's real-world jitter level. The receiving side fights to hold the reserve at the target, and that fight is audible. Raise **ASIO jitter buffer in milliseconds (Alt+I)** on the Audio profile tab to 25 ms or more and the graininess should disappear. Even pure-ASIO setups can't safely sustain a receive reserve below about 15 ms over real networks; aim higher on Wi-Fi.

 ### I can't see my friend in Discovered peers not on a server

   * If you're on the same local network: are both computers on the same Wi-Fi or Ethernet network? Some guest networks deliberately keep devices from seeing each other.
   * If you're using Tailscale: type their Tailscale address into “Add peer by IP” (Connectivity tab, Alt+A) once. After that, both sides see each other automatically.
   * Check that Windows Firewall isn't blocking RemSound. The first launch usually asks; if you said no, you'll need to allow it by hand.



 ### A peer rebooted or changed address and the sound didn't come back

 RemSound keeps trying the address you connected to, so when a peer comes back at the same address — the usual case after a reboot — the sound returns on its own within a few seconds, with nothing for you to do. If the peer comes back at a _different_ address (because their router gave it a new one, say), RemSound follows it there on its own, within a few seconds of hearing its announcement — unless the profile is locked to exact addresses, or the peer is somewhere its announcements can’t reach you (over a VPN with its old address remembered, say). Then reconnect by hand: pick it again from **Discovered peers not on a server** , or enter its new address with **Add peer by IP** (Connectivity tab, Alt+A). If the sound still doesn't return, the peer is genuinely unreachable — off, asleep, or a firewall is blocking the path.

 ### RemSound closed unexpectedly

 If RemSound ever closes on its own, it writes a small crash file into your logs folder (the RemSound folder → **user settings and logs** → **logs** , named `crash-` followed by the date and time). There's nothing you need to do with it — but if it happens, sending that file along with your report records what went wrong, so it can be tracked down and fixed.

 ### One side says “unreachable” even though sound is flowing

 The health check-ins use the same port as the audio, so if the sound gets through, the check-ins should too. If one side shows “unreachable” while the sound plays fine, make sure both computers are running the same version of RemSound — an older version on either end can speak a slightly different check-in language.

 ### My other audio went silent or crackly when I selected an ASIO driver

 You probably picked Realtek ASIO. It's a generic driver, not tied to Realtek hardware, and it tends to grab whatever Windows treats as the default sound device — usually the same one your screen reader is using. Set the ASIO driver picker back to _(none)_ , or pick a different ASIO driver.

 ### The device list shows old devices that are no longer plugged in

 RemSound reacts the moment a device is plugged in or unplugged, so an unplugged device should disappear within a second or two. If one lingers, restart RemSound — Windows' own device list occasionally needs a nudge.

 ### No sound after the computer wakes from sleep

 RemSound notices when the computer has just woken up, waits a moment for any USB sound devices to come back to life, then stops all its audio and starts it again from scratch — you'll briefly see a small “Reconnecting to audio driver” window while it does. It then puts back the buffer sizes the automatic tuning had settled on before the sleep, so the delay should be what it was when you left. Sound should resume on its own. If sound still doesn't come back, click on the ASIO driver picker on the Audio inputs and outputs tab and re-pick the same driver (or pick _(none)_ and then re-pick your driver). That triggers the same full rebuild manually. As a last resort, quit and reopen RemSound.

 ### A sound card you were listening through was unplugged

 If a sound card you're playing received audio through is unplugged and then plugged back in, RemSound now re-opens it on its own and the sound resumes — you don't have to re-tick it in the output list. This works when the card comes back as the same Windows device, which is the usual case when you plug it into the same socket. If you move it to a different USB socket and Windows treats it as a brand-new device, just tick it again in the output list.

 A sound device your profile ticks keeps its tick when you save, even if it isn't plugged in at the time — whether it was missing when the profile loaded or you unplugged it while RemSound was running. An ASIO interface that's switched off keeps its channels' ticks the same way. Next time it's there when the profile loads, it's ticked again. And if you switch the interface on after RemSound has started, RemSound tries its driver once more when Windows notices it, fills the ASIO lists, and ticks the channels your profile ticked. If it comes back while RemSound is running, it comes back unticked, except a Windows output you listen through, which is ticked again for you; tick it yourself before you save, or the save leaves it out.

 ### Something you're sending from was busy, or stopped

 If one of the things you've ticked to send can't be opened — say another program has the device to itself — or it stops working while you're sending, RemSound tries it again every few seconds and starts sending it as soon as it can. You don't have to untick and re-tick it. Everything else you're sending carries on untouched while it does, including anything on ASIO. The log (if logging is on) says when it's still being tried and when it's back.

 ### Saving the service profile says “access denied”, or the service's log files won't open

 Your account has lost its permissions on the service's settings folder — this could happen after some older-version reinstalls, and it also blocks the service itself from starting properly. The fix is one menu item: **Service → Repair service folder access**. Windows asks for administrator permission once, the folder is put right, and saving and log-reading work again. RemSound also spots this state by itself when it starts and offers the same repair automatically, and the service applies the fix on its own when it installs a RemSound update — so on an up-to-date machine you should never actually meet this problem. If you're signed in to a different Windows account from the one that set the service up, this isn't a fault: only that account can change the service's settings, and RemSound says so once rather than offering the repair. If that account is gone for good, Service, Repair service folder access still works: RemSound warns that it hands the service to your account, and asks first.

 ### UPnP says “no router found” even though my router supports it

 The most common reasons:

   * UPnP is disabled in your router's settings. Look for a checkbox marked “UPnP”, “NAT-PMP”, or “Allow apps to automatically forward ports” in the router's admin page. It's often off by default.
   * Your Windows network is set to “Public” rather than “Private”. Public mode blocks the discovery messages RemSound uses to find the router. In Windows' network settings, switch your home network to Private.
   * Your computer is on a Wi-Fi guest network or a corporate / hotel network. These networks usually block the kind of discovery messages UPnP needs.



 If none of those apply, just fall back to Tailscale — it works without involving the router at all.

 ## 27\. Glossary

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
 Heartbeat| A small message that connected computers exchange every second to confirm they can still reach each other and to measure the round-trip time. It travels on the audio port (47830 by default).
 Discovery| The way RemSound computers find each other on the network without you having to know each other's addresses up front.
 Tailscale| An easy-to-use VPN that puts your computers on a private network together. The simplest way to connect RemSound across the internet without changing your router settings.
 UPnP| Short for “Universal Plug and Play”. A feature most home routers support that lets an app ask the router to open a port for incoming connections, without the user having to log into the router. RemSound uses UPnP (and its newer relatives NAT-PMP and PCP) to set up port forwarding automatically when you tick “Automatically open my router for incoming connections” in Preferences, on its Connectivity tab. Off by default.
 NAT| Short for “Network Address Translation”. The way your router lets several computers share a single internet connection — one public address on the outside, lots of private addresses on the inside. Most home networks use NAT, which is why you usually need port forwarding (or UPnP, or a VPN) for two computers in different places to reach each other directly.
 Carrier-grade NAT| An extra layer of NAT that some internet providers (especially on mobile broadband and some cable connections) put in between your router and the rest of the internet. Your home router opens a port fine, but the provider's NAT in front of it still blocks incoming connections. RemSound's UPnP status line warns you when this is the case — the way through it is a VPN like Tailscale, or a server you run yourself.
 Auto-tune| RemSound automatically adjusting the jitter buffer based on how evenly packets are arriving. Off by default; turn it on with the Continuous auto-tune checkbox on the Audio profile tab.
 Profile| A saved snapshot of every RemSound setting and choice — device ticks, send / receive states, codec, jitter buffer, peers, ASIO driver choice, the lot. (Keyboard shortcuts and most of Preferences are the exception — they’re kept on the computer and shared by every profile.) Stored as one settings file. You pick one at startup, and can switch with File → Open profile.
 New profile| An entry in the startup profile picker that begins a session with all the defaults — nothing ticked, no peers, no saved name. A clean starting point for a new profile, or for a one-off session you don't plan to save.
 Lock to audio clock| A sending-side timing mode that takes its timing straight from the sound device's hardware clock instead of from Windows. Removes a few milliseconds of wobble at tight latency targets. RemSound now does this always — it used to be a checkbox on the Audio profile tab, but it is on permanently and no longer a setting.
 Concealment| A receiving-side feature that fills brief gaps in the playback reserve with a small noise burst (the default) or an obvious click. You choose which on the Audio profile tab, in the _Artefact sound type_ list. Opus also has its own repair of lost packets on top of this.
 Remote control| A RemSound feature that lets one connected peer adjust another peer's listening volume (or toggle their receive mute) using global hotkeys. There are two sets of commands: one adjusts the receiver's RemSound volume slider, the other adjusts the receiver's Windows volume. Off by default on both ends; the receiver opts in via “Accept remote volume commands from peers” in the Preferences dialog (Ctrl+P), and the sender sets up hotkeys in the Keyboard shortcuts dialog (Ctrl+K). Designed for the “I'm NVDA-Remote'd into my desktop and want to nudge the laptop's volume” case. See Remote control.

 * * *
