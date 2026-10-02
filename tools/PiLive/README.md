# Live tests against a RemSound server

Tests that run from a Windows PC against a real relay on a test box (a Raspberry Pi on the same network), for checking
server code where it really runs. Double-click `pi-live-tests.cmd`, press 1, 2 or 3, and read the report it writes to
`reports\`.

1. **Everything**: all the checks below.
2. **RemSound's own checks through the server**: the self-test steps that can run against a real relay
   (`REMSOUND_TEST_LIVE_RELAY`): server status, hearing yourself, Jamulus-style sending and the server's metronome.
3. **Network timing**: 30 seconds of "hear myself" at the ASIO lane's rate (one packet every 0.667 ms, padding only, in a
   group of its own) while the box timestamps every packet in and out of its network card. Fails if the relay holds a
   packet for over 1 ms (one in a thousand) or loses more than 0.5 % of them. The hold-ups on the way there and back are
   reported, not judged: they belong to the PC and the network.

Before anything else it checks the box is reachable, the relay is running, and that **nobody is on the server**: if
anyone is, it stops there, so a live test never disturbs a real session. It also notes whether the box runs the same
server version as this tree.

## Settings

`pi-live.local.json` beside the scripts (git ignores it, and the `reports` folder):

```json
{
  "host": "the box's address",
  "user": "an SSH user with passwordless sudo",
  "key": "%USERPROFILE%\\.ssh\\the-key",
  "port": 47830,
  "iface": "eth0"
}
```

## The files

- `pi-live-tests.cmd`: the double-click runner.
- `pi_live.py`: runs the checks and writes the report.
- `pi_sniff.py`: copied to the box for the timing check and removed after; it only listens.
- `echo_probe.py`: the PC's side of the timing check.

Needs Python 3 and OpenSSH on the PC, and Python 3 on the box.
