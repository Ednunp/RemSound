# RemSound v5.7

Stronger security, and it works with every version again.

This release puts the password handling back the way it was, so RemSound talks to older versions and the iPhone app again. You still set a password — your audio is always encrypted — RemSound just suggests a strong one now instead of requiring it. If you already use a good password, you won't notice any difference.

## Signed updates

Every release is now digitally signed, and the updater refuses anything that isn't genuinely from us — so even if the download page were ever tampered with, a fake update couldn't install itself on your machine.

## Remote volume, password-protected

The remote volume and mute controls are locked to your password, so only someone who shares it can use them. (Both ends need 5.6 or newer for the remote-volume feature; ordinary audio works with any version.)

## Set the machine's volume when the service starts

In the service's Additional options you can have an unattended machine unmute itself and set its Windows volume to a level you choose — on the first start after each boot, or on every service start.

## Updates on your schedule

In Preferences you can restrict automatic updates to a daily time range — say 1am to 6am — so an update never closes RemSound and interrupts you. Found outside the range, it quietly waits and installs the moment the range opens.

## Also in this release

- The app releases its high-priority and keep-awake settings when you're not actually streaming — kinder to laptops left idling in the tray.
- Diagnostic logs cap their own size on long sessions, and old crash reports are tidied automatically.
- The remembered-applications list explains itself when empty.
- A large amount of behind-the-scenes hardening from a full security audit.
