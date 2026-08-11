# RemSound v5.8

A repair for the lock-screen service's settings folder.

Some machines ended up with the service's settings folder locked so tightly that nothing could use it — saving the service profile failed with "access denied", the service's log files wouldn't open even in Notepad, and on some machines the service itself couldn't start. It came from a permissions bug in a recent release, and reinstalling didn't clear it.

This release fixes the cause and heals affected machines automatically:

- The service applies the fix on its own when it installs this update — for most people that's it, nothing to do.
- RemSound also checks the folder every time it starts, and offers a one-click repair if it finds a problem.
- And there's a "Repair service folder access" item in the Service menu you can run any time. One administrator prompt, and saving the service profile and reading the logs work again.

## Also in this release

- The service's log files are readable from every account on the machine again, so you can always open them in Notepad if you need to look at one or send it in. (The service's settings stay protected as before.)
- If a service action fails, the message now says what actually went wrong instead of showing a bare error code, and the reason is recorded in the service's own log.
- The repair, and the folder protection itself, now always apply to the account that's actually using RemSound — even on PCs where a different account's password is typed at the administrator prompt.
