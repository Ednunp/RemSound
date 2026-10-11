# RemSound v6.2

**Listen in a web browser.** Hear what RemSound sends from a web browser on another computer, with nothing to install there: handy where you can't install RemSound, such as at work. It goes through Tailscale Funnel, behind a password of your own. Press Web listener on the Connectivity tab to switch it on, set the password, copy your web address and turn on Funnel. The page says which computer you're hearing, and has Stop listening, Volume, Pan and a three-band EQ. It can start by itself when it loads, each browser can save its own settings, and up to four people can listen at once.

**Audio cues for each profile.** Preferences, Audio cues now has two lists. Global sounds are the six that belong to RemSound itself: profile saved, profile switched, the profile menu opening, update, and context help opening and closing. Profile sounds are everything else, kept with each profile, so a jamming profile can stay quiet while another keeps every sound. Keyboard clicks go with the profile too, and Turn all audio cues off silences the lot. The sounds you have now carry over the first time you open each profile.

**Fixes:**
- Handing over between RemSound and the send-only service on a server is more dependable. The one stepping aside goes quiet before it says goodbye, so it no longer leaves a ghost that keeps a phone out for up to a minute. The goodbye now goes three times, so one lost on the way no longer means 5 to 7 seconds of silence.
- A phone, an older app or a profile without a password now takes over a place on a server straight away, instead of waiting up to a minute.
- The log says whether RemSound and the service count as the same person on a server, and when the service couldn't be told to step aside.

The quicker hand-over for phones, older apps and profiles without a password needs server-v2.17.

RemSound updates itself. To install by hand, download **RemSound-v6.2.zip** (a zip of the program folder) from the files below, close RemSound, and extract it over your RemSound folder.
