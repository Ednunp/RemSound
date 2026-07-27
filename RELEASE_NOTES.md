# RemSound v5.6

**IMPORTANT: everyone must update.** This version strengthens the encryption maths, so a 5.6 machine **cannot exchange audio with any older RemSound** — until both sides are on 5.6, you'll hear nothing between them. Update every machine you connect with, including any running the background service (it updates itself from the app). Remote volume commands also need both ends on 5.6.

## Stronger passwords, enforced

The profile password is what protects your audio, so from this version RemSound refuses to stream on one that's easy to guess. Passwords must be at least 8 characters and not a common word — if yours is shorter, RemSound tells you the moment you try to stream and walks you through choosing a better one. Three unrelated words with a number, like `kettle9tiger42moon`, is easy to type and very hard to guess. Remember to set the **same** new password on every machine you connect with.

## Signed updates

Every release is now digitally signed, and the updater refuses anything that isn't genuinely from us — so even if the download page were ever tampered with, a fake update could not install itself on your machine. (This release ships the checking code; it protects every release from here on.)

## Remote volume, now password-locked

The remote volume and mute commands are sealed with your profile password, so only someone who knows it can adjust your machine — nobody on the network can fake a command and mute your screen reader. A captured command can't be replayed later either.

## Set the machine's volume when the service starts

In the service's **Additional options** you can have an unattended machine unmute itself and set its Windows volume to a level you choose — on the first start after each boot, or on every service start.

## Updates on your schedule

In Preferences you can restrict automatic updates to a daily time range — say 01:00 to 06:00 — so an update never closes RemSound and kills your sound mid-session. Found outside the range, it quietly waits and installs the moment the range opens. The manual "Check for updates now" button is never restricted.

## Also in this release

- The app releases its high-priority and keep-awake settings when you're not actually streaming — kinder to laptops left idling in the tray.
- Diagnostic logs cap their own size on long sessions, and old crash reports are tidied automatically.
- The remembered-applications list explains itself when empty (apps join it the moment you tick them).
- The public relay gained anti-abuse protections; a later relay update will require 5.6, which answers its address checks automatically.
- A large amount of security hardening from a full audit: service-folder lockdown, replay protection, counter-based encryption nonces, and more.
