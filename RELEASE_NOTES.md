# RemSound v6.0

RemSound now works inside your music software.

There is a plugin. Put it on a track and one person goes on that track: a track you are sending goes out to your peers, and a peer you are receiving arrives on a track of their own, where you can record them, shape them and mix them like anything else. Add it twice for two people and each gets their own track.

It is a VST3 plugin, so it works in Reaper, Cubase, Studio One and anything else that loads VST3.

## Putting it on your machine

Open the new **DAW plugin** menu (Alt+G) and choose **Install plugin**. It goes into your own plugin folder, so Windows does not ask for an administrator password and nothing outside your account is touched. Restart your music software and RemSound appears in its list of effects. **Remove plugin** takes it away again, and removes only the files it put there.

## How it works

Keep RemSound open while you work. RemSound holds the connection to your peers and the plugin asks it for audio, so your password, your peers and your audio settings all stay where you already set them. The plugin window asks only what that track is doing and, if it is receiving, who from.

When a track takes somebody, they stop coming out of RemSound's own output — so you never hear anyone twice. Let the track go, or close your music software, and they come straight back. There is nothing to set.

If you have set volume, pan or EQ for a peer in RemSound, that comes through to the track as well. Untick **Apply pan and EQ to plugin audio** if you would rather have the raw signal and do the work in your DAW.

## Accessibility

The plugin window is built from ordinary Windows controls, so a screen reader reads it the way it reads RemSound itself — which is unusual for a plugin. If your music software makes that window awkward to reach, the same three choices are also plugin parameters, which every DAW lists and Reaper with OSARA reads out.

One limit worth knowing: your music software cannot compensate for the network delay automatically, so a track recorded through the plugin sits a little late and needs nudging back. That is a limit of how VST3 plugins report themselves.

## If you do not use a DAW

Nothing changes. RemSound listens for plugins on your own machine only, and never on the network. If you would rather it listened for nothing at all, untick **Let plugins connect to RemSound** in the DAW plugin menu.

Section 25 of the manual walks through all of it.

---

The rest of this release is a line-by-line pass over the send path, the receive path, recording and the plugin bridge. Most of it is things that were quietly wrong rather than things anyone had reported.

## Sending really does stop when you turn it off

With an ASIO driver selected, turning off **Send my audio** stopped the WASAPI side but left the ASIO side running. The driver kept handing over audio, and that audio was still being encoded, encrypted and sent to your peers. The switch looked off and was not.

Turning sending off now stands the ASIO lane down as well, without closing the driver — so there is still no pause when you turn it back on.

## Recording keeps all of what it heard

With both a WASAPI and an ASIO output ticked, each peer's own recorded track could lose part of itself. The two outputs run at their own speeds and hand over blocks of different lengths, and the recorder kept the length of the last block it saw rather than the longest — so the tail of the longer one was written over with silence. A rehearsal recorded that way is only discovered when you open the file.

Fixed, and measured: a peer arriving on both outputs now keeps every block, whichever order they arrive in.

A long recording also used to run past the 4 GB that a WAV file's own header can describe — about three hours of 32-bit float, four of 24-bit — and carry on growing while the header described something shorter, so a player reads part of it and stops. The recording now stops at the limit, says why in plain English, and leaves a valid file behind with all the audio it had.

## The Total latency box tells the truth

Several things were wrong in that box, and all of them made it read high, or read nothing.

Running ASIO only, it showed the WASAPI figures — which are zero when nothing is on that side — so it said the sound card added nothing, or that it was not receiving at all, however loudly your driver reported its real playback latency. It now reads the lane you are actually using.

The jitter buffer was being counted twice: once as the setting you chose, and again inside the measured figure sitting next to it. On a 34 ms buffer that added about 21 ms of nothing to the total. Each number now means one thing, and the three add up.

When you are only listening, the far end's microphone is on somebody else's machine and this end had no way to know what it cost, so it assumed 10 ms. Senders now state their own measured figure, and it can be a great deal smaller — 0.7 ms on a good interface. If the other person is on an older RemSound nothing breaks; this end simply falls back to its old estimate.

Automatic tuning was affected by the same family of faults. With two outputs of different kinds ticked, one output's timing could end up sizing the other's buffer — which is exactly what having two separate buffers is meant to prevent.

## Devices that come back on their own

Change anything about an output device in the Windows sound control panel — a level, a format — and Windows quietly kills any audio already playing through it. RemSound knew how to recover, but only ever checked when a device was plugged in or unplugged. Your device had not gone anywhere, so nothing checked, and you had no sound until you unticked it and ticked it again.

RemSound now notices by itself and re-opens the device within a few seconds. The same fix covers a device that comes back late after sleep or hibernate — common with wireless headsets, which take far longer to reappear than a USB interface — and a capture device that faults without disappearing.

## Things that could take the whole app down

A single malformed announcement from anything on your network could close RemSound outright. Announcements are now checked before they are believed, and anything bad is dropped and counted.

**Force close the other copy** looked for RemSound by name, which meant it also found the lock-screen service — something it can never actually be, since that runs in a different Windows session. Refused access, it escalated to an administrator prompt, and accepting that stopped your lock-screen streaming. It now only reaches copies running in your own session.

The uninstaller removed whatever folder it found its install marker in, with no check on what that folder was. If RemSound had been copied to the root of a drive, uninstalling it would have taken the drive with it. It now refuses drive roots, network roots and shared Windows folders.

The audio threads no longer lose their high-priority status for the rest of a session after one logged hiccup, a device disappearing part-way through a rebuild no longer crashes the app, and a recorder that throws is reported as a recorder problem rather than as a lost sound card.

## The plugin itself

A peer taken by a plugin track was being summed twice when you had both kinds of output ticked — about 6 dB hot, with the two copies drifting against each other. Fixed.

The bridge between RemSound and the plugin is leaner in the places that run on your DAW's audio thread: it no longer clears 32 KB of memory on every audio block, no longer converts audio a sample at a time, and no longer copies each incoming block twice. And a peer list that swapped one person for another without changing the count is now noticed.

## Waking the computer no longer leaves the sound late

If you put the computer to sleep or hibernate it with RemSound running — and especially if you unplug a headset first and plug it back in afterwards — the sound could come back noticeably later than it was, and take a while to recover.

The automatic buffer tuning was reacting to the wake itself. Everything that happens as a computer sleeps and wakes — devices vanishing and coming back, the network reconnecting, the audio drivers restarting — looks to the tuning like a bad connection, so it raised the buffer to cope, and then came down only slowly.

Now, when the computer wakes, RemSound stops all its audio and starts it again from scratch. Then it puts back the buffer sizes the tuning had settled on before the sleep, so you come back to the delay you left and nothing has to be relearned. It takes them from the few minutes before the computer went down rather than the very last moment, because unplugging a headset just before sleeping is enough on its own to push the buffer up.

A headset reconnecting afterwards is recognised as exactly that rather than as network trouble, and a device that has just reconnected is given time to settle before its timing is believed.

Leaving RemSound running for hours without sleeping was measured separately, over eight hours, and nothing crept up. The problem was the wake, not the time.

## An output you have finished with can no longer spoil the one you are using

If you had both kinds of output running and one of them stopped — you switched it off, the far end stopped sending on it, a driver went quiet without saying so — RemSound carried on preparing audio for it anyway. That audio had nowhere to go, so it piled up and was thrown away, over and over, for as long as the app stayed open.

The waste was not the problem. The work of doing it took real processing time away from the output you were still listening to, which then started running short, and the automatic tuning quite correctly raised your buffer to cope. So an output you had finished with could make the one you were using audibly late, and the buffer reading would show a figure that belonged to the dead output rather than the live one.

RemSound now watches whether each output is actually taking audio, rather than only whether it is switched on. An output that stops taking audio stops being prepared for within a few seconds, and anything that was playing through it moves across to an output that is still running so you keep hearing it — once, not twice over — and a recording carries on from that output too. Both kinds of output are treated the same way.

With no ASIO driver chosen, an ASIO output still ticked from before can no longer make a peer play twice, louder and with a phasey sound.

## The "older version" warning names the likelier cause

The warning about a machine "running an older version" has been reworded. It appeared whenever no password fingerprint arrived, which is equally what a current version sends when that person has no password set — so it could tell you to go and update a machine that was already on the same version as yours. It now names both possibilities, the likelier one first.

## Several capture sources staying together

If you send more than one input at once — a microphone and a system-sound loopback, say, or two interfaces — each one runs on its own crystal, and RemSound's mixer runs on none of them. Every source was therefore fractionally fast or slow, and two of them pulled apart from each other over a session. At an entirely ordinary 50 parts per million that is about 3 ms a minute, so roughly 18 ms after six minutes, and it kept going.

Nothing corrected it. A source running fast eventually filled its buffer and the oldest audio was thrown away — that source jumping forward. A source running slow emptied its buffer and had silence padded in. Neither happened until the error was already a quarter of a second, and by then you had heard it.

Each source is now held on the mixer's clock by the same gentle correction that has been holding sound cards steady on the receiving end for months: a rate trim of well under a tenth of a percent, far too small to hear as pitch. If you send only one source, which is what most people do, nothing is touched at all — there is nothing for it to stay aligned with.

Reported by Anthony Reyers from real use.

## Keyboard shortcuts that go where they say

Every Alt key in the main window and the dialogs has been checked against what the screen says, in every tab and with every combination of outputs ticked. These were wrong:

- **Total latency** said Alt+M but answered Alt+T, which is the auto-tune checkbox. It is Alt+M now.
- With an ASIO driver chosen, Alt+I belonged to both the ASIO jitter buffer and the auto-tune interval. The interval is Alt+N now.
- **Packet size** said Alt+P but Alt+P went to the codec box. It goes to Packet size now.
- In **Add EQ band**, Alt+S, Alt+E and Alt+G each landed on the box below the one they name. They land on their own boxes now.
- In **Rename peer**, Alt+N landed on Clear custom name, so pressing Enter cleared the name. It lands on the name box now, and Cancel has Alt+A.
- In **Preferences**, Alt+A belonged to both the auto-save list and Accept remote volume commands, so it could switch remote volume commands on or off by accident. Accept remote volume commands is Alt+V now, and Close has Alt+C.
- The service profile's Cancel has Alt+N, and **Additional options** now has a Cancel button that Escape presses. What you set there is only kept if you then save the service profile.
- In **Keyboard shortcuts**, Close has Alt+O.

The gate now walks every tab and dialog this way on every build, so a shortcut that points at the wrong control fails it.

## Other fixes for keyboard and screen reader users

- The peer details box no longer rewrites itself every second while you are reading it.
- If RemSound cannot start a recording, the message now comes to the front, even when you started it with the hotkey while RemSound was minimised.
- Changing a keyboard shortcut no longer says the profile has unsaved changes. Shortcuts are saved on this computer straight away.
- The status readout and the connected peers list now agree with the connect sound about whether a peer is connected. A short heartbeat hiccup while audio is still playing no longer shows "Not connected".
- In Preferences, the **Choose sound** list now shows your own file as an entry when you have picked one with Browse. Choosing a built-in sound really does switch to it, and you can arrow back to your own file before closing. It also reads as "Choose sound" to a screen reader, as it does on screen.

## When logging is off

The per-second housekeeping that reaps a plugin whose DAW has died, and the warning that recording is falling behind, were both sitting behind the "write log file" switch. With logging off — which is how most people run — neither happened. Both are always-on now.

## Better logs when you need them

If you do turn logging on, it now records what you change. Moving the jitter buffer, switching auto-tune on or off, changing the volume, send mode, codec or send rate, and every preference in the Preferences window all leave a line — each stamped with which outputs you had ticked at the time. Turning logging off still means nothing at all is written.

None of that changes what you hear. It means that when something does go wrong, the log can say what was actually going on instead of leaving it to guesswork.
