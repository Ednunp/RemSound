# RemSound

**Free Windows app for sending live audio between two computers — across a house, across a city, or anywhere your internet reaches. Encrypted end to end, low delay, great quality, fully accessible to screen-reader users.**

[**Download the latest version**](https://github.com/Ednunp/RemSound/releases/latest)  ·  [**Read the user manual**](MANUAL.md)

---

RemSound is for anyone who wants to get live audio from one Windows PC to another with as little delay as possible — listening to one machine while you work at another, playing music together over the internet, co-hosting a podcast, and plenty more besides.

You sit at one computer, RemSound captures whatever is playing — a track in your music software, a video call, system sound from anything else running — and sends it cleanly to another computer where it plays through speakers or headphones in real time. The person at the other end hears what you're hearing, with a delay measured in milliseconds rather than seconds.

It's also fully accessible. The interface was designed with screen readers (NVDA in particular) in mind from day one. Every button has a keyboard shortcut, every status line is read out clearly, and there are no menus or controls that need a mouse to reach.

## Private by default — your audio is encrypted

Everything RemSound sends is **encrypted end to end**: scrambled the moment it leaves your computer and only unscrambled at the other end, so nobody in between — your internet provider, a shared Wi-Fi, anyone watching the line — can listen in. You no longer need a VPN just to keep a private connection private.

It works with a simple shared password. You and the person you're connecting to use the **same password**, and only the two of you can hear the audio — get the password wrong and nothing comes through. RemSound stores a password on each profile and walks you through setting one. And it all adds no delay you'd ever notice.

## What you can do with it

* **Listen to one of your computers from another room.** Sit at your laptop and hear what's playing on your desktop. Walk around the house — the sound follows you.
* **Play music together over the internet.** Two musicians at different houses can play along together with very low delay. Much faster than a video call, fast enough that timing-sensitive playing works.
* **Mix everyone as you go.** With several people connected, set each person's volume, lean them left or right, and shape their tone with a built-in equaliser (simple 3-band, 12-band graphic, or a full 16-band parametric) — all live, and it's captured in your recordings.
* **Send a finished mix to a producer or client** in real time, without uploading a file and waiting.
* **Record what comes through the connection** to WAV, MP3, OGG-Opus, or FLAC — the whole mix, or **each person on their own separate track** to mix afterwards.
* **Give the people you connect to names that stick**, and see who's connected and what they're sending at a glance.
* **Drive it from the command line.** As well as its normal window, RemSound takes command-line options — list your audio devices, run a self-test of the whole capture-to-playback path, write a diagnostics report to send for help, or start straight into a profile from a shortcut or script. See [Command-line options](MANUAL.md#22-command-line-options) in the manual.

## Three quality settings, simple choice

Inside RemSound there's just one main decision: which quality and delay you want.

* **PCM 48K 24 bit — uncompressed.** The best possible sound. Uses about 2.3 megabits a second. Use it when both computers are on the same local network.
* **Opus, broadcast quality — loss tolerant.** Compressed, very good sound, only 200 kilobits a second. Robust against patchy connections. Use it across the internet.
* **Opus, live latency — for jamming and monitoring.** Compressed, ultra-low-latency mode. About 5 milliseconds of delay added by the codec itself, very close to PCM. Best when you and the person on the other end are playing along together over a clean network.

## How to install it

1. Go to the [latest release](https://github.com/Ednunp/RemSound/releases/latest).
2. Download the file called `RemSound-v3.3.zip` (the version number changes over time — pick whichever is newest).
3. Extract the zip into a folder of your choice.
4. Double-click `RemSound.exe` and away you go.

The first time you launch, RemSound will offer to install Microsoft's .NET 10 Desktop Runtime if you don't already have it. Free, just say yes.

After that, RemSound updates itself. Help → Check for updates pulls the next version, or you can tick a box in Preferences and let it install updates quietly in the background.

## What you'll need

* **Windows 10 or 11.** Some users run it successfully on Windows 7, but it's not officially supported there.
* **Another person running RemSound** on their own Windows machine.
* **A way for the two machines to reach each other on the network.** Both on the same Wi-Fi works. Both on the same [Tailscale](https://tailscale.com) network works (free and easy to set up). Or both pointed at the public RemSound relay (also free, no setup).

## RemSound on Android

There's a companion **Android receiver**, so a phone or tablet can pick up RemSound audio — handy for listening on the move while a Windows machine does the sending. It's a separate community project, built and maintained by **[Aryan Choudhary](https://github.com/aryanchoudharypro)**, who is a screen-reader user himself and has tuned the app for TalkBack. It isn't part of RemSound and isn't maintained by us, but it speaks the same protocol and we're glad to point you to it.

**Get it:** [RemSound Android — Releases](https://github.com/aryanchoudharypro/RemSoundAndroid/releases) — download the latest `app-release.apk`.

## Learn how to use it

The full user manual is right here on GitHub: **[Read the user manual](MANUAL.md)**. It covers getting connected for the first time, every setting and what it does, troubleshooting tips, and a glossary at the end. It's the same manual you can press F1 to read from inside RemSound, so you can read it before installing if you want to see what you're getting.

## Questions or problems?

[File an issue](https://github.com/Ednunp/RemSound/issues/new). Bugs and questions are welcome and someone will get back to you.

## Who made this

RemSound was built to solve a specific problem: hearing the audio from a powerful computer while sitting at a lighter one, with that powerful machine running remotely. There are other programs that can move audio between PCs, but none of them did it quite the way I wanted, so I built my own. It's free, open-source, and yours to use however you like.

## Licence

MIT. See `LICENSE`.
