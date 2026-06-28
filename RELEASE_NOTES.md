# RemSound v4.9

Lock a profile to fixed addresses — for when you want one exact address and nothing else.

## Lock to exact peer addresses

RemSound normally tries to be helpful about addresses: it finds the other computer by the name it advertises on your network, and if that computer turns up at a different address — a new IP after a reboot, or a second address over a VPN — it follows it there so your sound keeps flowing. Most of the time that's just what you want.

Sometimes it isn't. If a machine is reachable at two addresses at once and you only ever want one of them, there's now a tickbox on the **Connectivity** tab:

**Lock to these exact peer addresses, no matter what (Alt+L)**

With it ticked, that profile uses only the exact addresses you set. RemSound won't look the other computer up by name, and won't switch to any other address it discovers. If the address you set stops working, the connection simply waits (or drops) until that exact address is reachable again, rather than wandering off to another.

It's off by default and saved with the profile, so you can lock one profile down while another keeps the automatic behaviour. When you use it, set your peers by their IP address (**Add peer by IP**) so there's no doubt which address is locked in.

## Compatibility

**v4.9 talks to v3.3 through v4.8 with no trouble** — the over-the-network format is unchanged, so you don't have to update both ends at once. (Everyone still needs **v3.3 or newer**, where end-to-end encryption came in.)

## Install

1. Download `RemSound-v4.9.zip` from this release.
2. Close RemSound.
3. Extract the zip **over your existing RemSound folder**, overwriting program files when prompted. The zip is program files only — it won't touch your settings, profiles, logs or recordings.
4. Run `RemSound.exe`.

## Upgrading

**From v3.6 or newer:** Help → Check for updates installs v4.9 with the in-app updater — and if it can't finish, it puts your old version back exactly as it was.

**From v1.9–v3.5:** Check for updates works, but uses your current version's older updater for this one hop. If auto-update has been failing on your machine, install by hand using the steps above.

**v1.8 and earlier:** the auto-updater in those versions can't install updates — install by hand using the steps above.
