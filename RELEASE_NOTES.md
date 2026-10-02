# RemSound v6.1

RemSound now keeps time with you, and tells you more about your server.

**The metronome.** A click of your own, at your tempo and time signature, in RemSound or on a track in your music software. On a server, anyone can be the master metronome: the server keeps everyone's beat, and gives each of you your click early by exactly how long your sound takes to reach it, so you all land together. Start, stop, tempo and volume all have hotkeys.

**Push to talk.** Hold a key to talk (Scroll Lock unless you choose another), double tap it to stay on, or let your voice open it when it gets loud enough. Recording and the DAW plugin can skip it.

**Hear yourself through the server.** Press **Unmute myself to test round trip on server** and the server sends your own sound back to you, so you hear the whole round trip.

**Server status.** The Server window now shows how long you've been on, your round trip to the server, how steadily your sound reaches it, who in your group is there, how many people are on the server, and its version. The tray says "connected to 1 server, 4 peers".

**Out-of-date servers.** RemSound checks a server before connecting. If it's older than the newest release, you're warned before you connect, every time, and can go straight to the latest server code on GitHub.

**Lower delay with ASIO.** When both ends play through ASIO, RemSound sends small Jamulus-style packets automatically.

**Also new:**
- **Mute received audio** on the Audio inputs and outputs tab (Alt+M), with a global hotkey: silence on your outputs without stopping anything.
- A **Listen to the metronome** button in Metronome settings, so you hear each change as you make it.
- Sounds for connecting to and disconnecting from a server.
- Global hotkeys for unmuting yourself on a server and for the metronome's volume; Metronome settings lists its own shortcut keys.
- Each person's total latency in the status.
- Record, Save the last minute of the network, to send in when something sounds wrong.

**Fixes:**
- Up arrow now turns a volume, EQ or level slider up.
- Uncompressed sound no longer goes flat when packets are lost.
- A server is never mistaken for a person.
- Save as now puts the new profile in Recent profiles.
- After Enter, typing in the jitter buffer box replaces the number instead of adding to it.
- A third RemSound plugin no longer crashes REAPER.

Server status and hearing yourself need server-v2.16 or later.

RemSound updates itself. To install by hand, download **RemSound-v6.1.zip** (a zip of the program folder) from the files below, close RemSound, and extract it over your RemSound folder.
