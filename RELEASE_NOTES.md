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
