using System.Reflection;

namespace RemSound.App;

/// <summary>
/// About dialog. Shows the running version, a short blurb about RemSound and the latest
/// release notes (built in below — bumped per release alongside the project's
/// <see cref="System.Version"/> property).
///
/// Layout follows the same NVDA-friendly conventions the rest of the app uses: a small
/// modal dialog with a heading label, a read-only multi-line text box for the notes that
/// the user can tab into and arrow through, and a Close button as the AcceptButton /
/// CancelButton. Escape dismisses.
/// </summary>
internal sealed class AboutDialog : Form
{
    /// <summary>Markdown-ish release notes shown in the About box's scrolling text area.
    /// Bumped per release. Keep it short — the canonical release notes also live on the
    /// GitHub Releases page, which the user can reach via the Help menu's "Check for
    /// updates" path.</summary>
    private const string ReleaseNotes =
        """
        RemSound v3.8

        You can now start a brand-new profile at any time. A
        new "New profile" item at the top of the File menu (or
        Ctrl+N) opens a fresh, unsaved profile, so you can set up
        a profile for a different connection from scratch — even
        when RemSound is set to start straight into a specific
        profile and you'd otherwise never see the picker. If your
        current profile has unsaved changes, it offers to save
        them first. The startup picker and the window title now
        say "New profile" too, where they used to say "blank
        template".

        And the "you and the other person have different
        passwords" warning now stays on screen until you press
        OK, instead of flashing away before you could reach it.

        RemSound v3.7

        Changing audio devices mid-session is smoother. A quick
        run of device changes now settles for a moment and
        rebuilds the sound engine once, instead of several times
        in a row — so reconfiguring no longer crackles.

        The latency auto-tuner keeps its head when the computer
        hiccups. A single brief stall no longer makes it balloon
        the buffer; it only raises the cushion when late audio
        keeps coming.

        The microphone-privacy warning now also catches blocks
        aimed at RemSound alone, and blocks set by an
        administrator policy — kinds of block Windows applies
        without showing you a switch.

        RemSound's log now records the loudness of what you're
        sending, the mic-privacy check's verdict, and every
        device change — so if something goes wrong, the log can
        prove what happened instead of leaving us guessing.

        RemSound v3.6

        Updating is far more reliable. RemSound now installs its
        own updates, step by step, instead of handing off to a
        Windows script that could quietly fail. If an update can't
        finish for any reason, RemSound puts your previous version
        back exactly as it was and tells you — so a failed update
        can never leave you stuck or half-installed. Your settings,
        profiles, logs and sounds are never touched.

        A couple more notices now come to the front too: the
        "RemSound is already running" message, and the notes shown
        after an update when RemSound was tucked away in the tray.

        RemSound v3.5

        Recover a sound card you unplug: if a USB sound card
        you're listening through is pulled out and plugged back
        in, RemSound re-opens it on its own and audio resumes —
        no need to re-tick it.

        Each sound card now gets the right audio cushion
        automatically. RemSound sizes it to the card, so one that
        needs a little more gets it without any fiddling, and a
        fast card stays tight.

        A microphone-privacy heads-up: if Windows is blocking
        microphone access and you switch a mic on, RemSound warns
        you once, so you're not left wondering why no one can
        hear you.

        Warnings always come to the front now, even when RemSound
        is tucked away in the system tray — so you never miss one.

        Everything this machine keeps for you — settings,
        profiles, logs and your cue sounds — now lives in one
        tidy folder called "user settings and logs". It moves
        there automatically the first time you run this version.
        From now on, updates never touch that folder, so any
        custom cue sounds you put there are safe.

        Also: your volume and mute now come back correctly when
        you load a profile, plus a batch of under-the-hood
        reliability and tidy-up work.

        RemSound v3.4

        Quick profile switch: a new global hotkey pops up a
        list of all your profiles from anywhere — even when
        RemSound is in the system tray. Arrow to one, press
        Enter, and it switches straight away. It marks the
        profile you're on, plays a sound as the list opens
        and again when you switch, and stays in the tray if
        that's where it was. Unset by default; give it a key
        under Options → Keyboard shortcuts.

        A safety net for the Realtek ASIO driver, which leaks
        Windows resources and can make audio unstable: RemSound
        now spots it on startup and offers, just once, to
        disable it. Re-enable or disable it any time from the
        Options menu.

        Your screen reader now reads out global hotkeys when
        you move over the menu item or control they're tied to,
        so you can learn your shortcuts just by arrowing around
        — no need to open the shortcuts dialog.

        Smoother long sessions on WASAPI: two machines' sound
        clocks drift apart by a hair over time, which slowly
        added delay. RemSound now corrects that continuously,
        so a WASAPI link stays as tight after three hours as it
        was at the start. (ASIO already kept itself in step.)

        Also: a new "profile menu open" cue; the profile-switch
        cue now plays the instant you switch, and no longer on
        a fresh start; faster reaction when you plug or unplug a
        device; and your settings now tuck into a "config"
        folder, moved there automatically the first time you
        run this version.

        RemSound v3.3

        Your audio is now encrypted, end to end, so you no
        longer need a VPN just to keep it private.

        How it works: every profile has a password. You and
        the person you're connecting to must use the SAME
        password — then your audio is scrambled on the way
        out and only unscrambled at the other end. Anyone in
        between hears nothing usable. The password is what
        lets you connect: matching passwords connect, and
        you hear each other; different passwords mean no
        audio (RemSound tells you when that happens).

        Setting a password: when you create a profile it asks
        for one. You can change it any time with File → Change
        this profile's password, and you can see and edit the
        passwords for ALL your profiles in one place under
        Options → Profile passwords. If you try to start
        sending or receiving on a profile that has no password
        yet, RemSound asks you to set one first.

        Adds almost no delay — the scrambling takes millionths
        of a second per packet, far less than the audio itself.

        Important: because the audio format changed, v3.3 can
        only talk to other v3.3 (and later) copies. Anyone you
        connect with needs to update to v3.3 too.

        Also in this release: connect, disconnect and the
        other cue sounds now play reliably whatever format the
        WAV is in (they used to be hit-and-miss with high-
        resolution files); the connect/disconnect cues now
        follow the actual audio, so you won't hear a false
        "disconnect" while sound is still playing; the system
        tray icon sticks to a peer's working address instead
        of hopping between a VPN and a LAN address (which could
        cause crackle on some setups); and RemSound now offers
        to show you what's new after each update (on by
        default; turn it off in Preferences).

        RemSound v3.2

        A new audio cue, plus the reliability work from
        the recent updates, rolled into one release.

        New "Update sound" cue: RemSound now plays a short
        sound just before it closes to install an update —
        for both manual updates and silent background ones.
        So a silent update no longer takes you by surprise;
        you hear it coming. Like every cue it's per-profile,
        can be muted, and you can swap in your own sound,
        all from File → Preferences → Audio cue sounds.

        Only one copy at a time: RemSound refuses to run as
        two copies at once. Open it while it's already
        running and it offers to switch you to the copy
        that's already going (now reliably bringing that
        window to the front, even from the system tray), or
        — if that copy is stuck — to force it closed and
        start fresh.

        Updates can't be blocked or doubled up: nothing can
        get in the way of an update's restart any more, and
        a single copy can't kick off two installs at once.

        Locked profiles stay locked: marking a profile as
        read-only now survives saving it.

        RemSound v3.1.3

        An important reliability fix. After an update, a
        chain of small faults could line up and leave
        RemSound misbehaving — in the worst case, more than
        one copy running at once with the sound getting
        louder and louder. This release breaks that chain.

        Only one copy at a time: RemSound now refuses to run
        as two copies at once. If you open it while it's
        already running, it offers to switch you to the copy
        that's already going (it may be down in the system
        tray), or — if that copy is stuck — to force it
        closed and start fresh. This makes the "stacking
        copies" runaway impossible.

        Updates can't be blocked any more: when RemSound
        updates itself and restarts, nothing is allowed to
        get in the way of that restart. Previously an
        "unsaved changes?" question could pop up at that
        moment and quietly cancel the update.

        Locked profiles stay locked: marking a profile as
        read-only now survives saving it. Before, if you
        deliberately saved a locked profile the lock was
        silently lost — which brought back the "save your
        changes?" question and, in turn, could trip up an
        update. Locked means locked.

        Everything else from v3.1 is unchanged.

        RemSound v3.1.2

        A small accessibility fix for the system tray
        icon, plus a clearer "check for updates" message
        for Windows 7.

        The tray icon's double announcement: if you use a
        screen reader, the tray icon could read out two
        states one after the other — for example "no peers"
        and then "1 peer" — every time you landed on it. It
        always happened in that same order. Windows stamps
        a tray icon's name at the moment the icon appears,
        which on a fresh start is a second before your other
        machine has reconnected — so the name was frozen as
        "no peers" while only the hover text caught up to
        "1 peer". RemSound now refreshes the icon itself
        whenever something real changes — a peer connecting
        or dropping, sending or receiving switching on or
        off, a recording starting or stopping — so the
        screen reader hears a single, current state.

        Recording in the tray: while a recording is running
        the tray now simply says "recording" rather than
        counting the seconds. A live timer would have made
        the icon flicker or read out a stale time; the exact
        length is on the main window if you need it.

        Windows 7 update message: on a Windows 7 machine
        that can't make a secure connection to the update
        server, "Check for updates" used to say "you're on
        the latest version", which was misleading. It now
        explains that the secure connection failed and points
        to the Windows updates that fix it, with a manual
        download link as a fallback. Windows 10 and 11 were
        never affected.

        Everything else from v3.1 is unchanged.

        RemSound v3.1.1

        Hot-fix for a small but annoying bug with the system
        tray tooltip after v3.1 first installed itself.

        What was happening: if the v3.1 auto-update fired
        while RemSound was set to start minimised to the tray,
        the tray icon's hover text could get stuck saying
        "RemSound — starting up" indefinitely, instead of
        switching to the live "X peers, sending, receiving"
        summary after a second or so. Showing the main window
        and then minimising it again was the workaround.

        What was actually going wrong: Windows registers a
        tray icon's tooltip with the shell at the moment the
        icon becomes visible. RemSound was setting an initial
        "starting up" string at construction time, and the
        shell tended to keep showing that on hover even after
        the live state had been computed and pushed through.
        The workaround (hide then re-show the icon) cleared
        the shell's cache. The fix is to compute the right
        live tooltip text once, just before the icon becomes
        visible for the first time, so the shell registers
        the icon with correct text from the start.

        Everything else in v3.1 is unchanged.

        RemSound v3.1

        Two big rounds of work on audio cues and the system
        tray, plus a handful of smaller fixes.

        New audio cue sounds:
          * Profile saved. Plays a short cue whenever a
            profile is saved (Ctrl+S or File menu Save as).
            Gives you an audible "yes, that took" so you
            don't have to look at the screen.
          * Profile switched. Plays a short cue whenever a
            profile finishes loading - at startup and after
            you pick a different profile. It plays the new
            profile's cue, not the old one's, so if you
            give each profile a different switched-to
            sound you can tell at a glance which profile
            you're now on.

        Per-profile custom cue sounds:
          * The Audio cue sounds list in Preferences now has
            six entries (Connect, Disconnect, Recording
            start, Recording stop, Profile saved, Profile
            switched), each with a tick to enable or
            silence it.
          * Two new buttons sit under the list. Play
            previews the currently-highlighted cue through
            your default Windows output. Browse opens a
            file picker so you can replace the default
            sound with any WAV file from your own disk.
            Right-clicking Browse offers "Use default
            sound" to undo the swap.
          * Both the tick states AND the custom sound
            choices are saved with the active profile, so
            different profiles can carry different cue
            palettes. A "quiet listening" profile can have
            all cues off; a "studio" profile can use a
            distinct set of sounds.
          * The default WAV files have moved from sitting
            loose next to RemSound.exe into a new sounds
            subfolder, keeping the install folder tidier.

        System tray icon redesigned:
          * Hovering shows a live summary - peer count,
            send / receive mode (WASAPI, ASIO, or both),
            and how long any current recording has been
            running. Refreshes every second.
          * Right-click menu reworked. The items are now
            Show RemSound (W), Enable sending (S, ticks
            reflect state), Enable receiving (R, same),
            Profiles (P, submenu of your recent profiles
            with number-key shortcuts), and Exit (X).
          * Show RemSound now reliably brings the window
            forward and focuses it, fixing a case where
            screen reader users had to Alt+Tab to actually
            reach the restored window.
          * Enable sending and Enable receiving toggle the
            state instead of always switching it on, so
            you can use them to turn off too.
          * The Profiles submenu lets you switch profiles
            from the tray without re-opening the main
            window. Number keys 1..5 jump straight to a
            slot.

        Bug fixes:
          * The tray icon's initial hover text was reading
            as "RemSound RemSound" on screen readers
            because the tooltip text matched the process
            name. Now reads cleanly as "RemSound - starting
            up" at first, then the live state takes over.
          * Recent profiles in both menus no longer
            announce a "Recent profile N:" prefix - they
            just read the profile name. The number-key
            shortcuts still work.

        No wire format change. v3.1 talks to other v3.0.x
        machines exactly as v3.0 / v3.0.1 / v3.0.2 did.

        RemSound v3.0.2

        Hot-fix for a slow memory leak in the receive side. If
        you leave RemSound running for many hours receiving
        audio, its memory use was creeping up steadily — small
        at first, but big enough after a day to slow the
        computer down and make audio feel slightly laggy.

        What was happening: RemSound uses a small library
        called Concentus to decode incoming Opus audio. In
        version 2.2 we switched it from a pure software
        version to a faster native version. The native version
        keeps some memory in Windows itself rather than in
        RemSound, and you're supposed to tell it explicitly
        when you're done with it. Our code wasn't doing that —
        it was relying on the system to notice and tidy up
        eventually, but a setting we use to keep audio smooth
        also stops Windows from doing that tidy-up. Result:
        memory built up over hours and never came back.

        The fix is twofold. First, RemSound now does that
        tidy-up properly every time it finishes with a piece
        of the audio pipeline — at the end of a session, when
        you change codec, and when a recording stops. Second,
        as a backstop in case any other piece of the puzzle
        ever has the same shape of bug, RemSound now does a
        quick "release any leftover memory" pass once every
        five minutes in the background. The pass doesn't
        affect audio — it runs on its own and is over before
        the next audio packet arrives.

        Reported by a user whose desktop was using 3.5 GB of
        memory after running RemSound continuously for nearly
        a full day. After the fix, expect memory to settle at
        around 100-200 megabytes and stay there for as long as
        you leave RemSound running.

        Nothing else has changed from v3.0.1 — same wire
        format, same codec list, same everything. If you've
        not noticed any slowdown after long sessions, the fix
        is still worth having because the leak was happening
        under the surface even if you didn't see it.

        RemSound v3.0.1

        Hot-fix for a bug in the "Automatically open my router
        for incoming connections (UPnP)" tickbox.

        On some network setups (machines with several network
        adapters, a VPN connected, or a router that doesn't
        answer the way RemSound's UPnP library expects) ticking
        that box could freeze RemSound's window — audio kept
        flowing, but you couldn't open the window again, even
        from the system tray. The only way out was to end the
        process from Task Manager.

        The fault was that RemSound was doing the router
        discovery on the same thread that draws the window, so
        a slow router (or no router answering at all) would
        block the window until it finished — which sometimes
        was never. v3.0.1 moves that work off to a background
        thread so the window stays responsive while RemSound
        looks for the router.

        Nothing else has changed from v3.0 — same wire format,
        same codec list, same everything. If you were already
        running v3.0 happily, this update fixes a problem you
        may not have hit; you can install it at your leisure.

        RemSound v3.0

        A big release with two things you'll actually notice:

        1) A new "live latency" Opus mode. RemSound now sends
           sound in tiny 2.5-millisecond chunks instead of the
           usual 10 or 20 ms. End-to-end delay drops to about
           5 ms of codec delay — close to PCM — perfect for
           playing along with someone in real time. Uses a bit
           more network bandwidth than the regular Opus mode
           but still a fraction of PCM. Best on a clean wired
           network. Pick it from the codec list as "Opus, live
           latency — for jamming and monitoring".

        2) After RemSound updates itself, it now reopens on
           the same profile you were using. So if a silent
           update fires mid-session, the session drops briefly
           while the new files swap in, then RemSound reopens
           with the same devices, peers and settings — you
           don't see the profile picker, and you don't have to
           be at the computer when it happens. The next time
           you launch RemSound yourself, your normal startup
           choice applies as before.

        Other changes:
          * The codec list is now three choices, with clearer
            names: PCM 48K 24 bit (uncompressed); Opus,
            broadcast quality (loss tolerant); Opus, live
            latency (the new low-latency mode). The old
            "Opus lower quality (10 ms)" middle option has
            been retired — it sat between the other two
            without a clear reason to pick it. If your saved
            profile was using it, RemSound silently picks
            broadcast quality for you — slightly more delay,
            more loss tolerance.
          * Save on a locked (read-only) profile now goes
            through when you ask on purpose. The lock still
            suppresses the automatic "save your changes?"
            prompt on close (its main job), but if you press
            Save deliberately, a one-time warning explains
            what's about to happen and lets you confirm or
            cancel. Once you tick "do not show again", future
            deliberate saves on a locked profile go through
            silently. Save as... is unchanged — it always
            works.

        Also includes everything from the never-separately-
        released v2.2 work: a native Opus encoder that puts
        much less load on Windows' memory manager (about 97 %
        less per-second memory churn while sending Opus),
        various small CPU and memory tidy-ups, and new
        diagnostic log columns (cpu, memMB, wsMB, allocKBps,
        captureMs / sendMs / recvMs / renderMs).

        Compatibility note — please read:

        v3.0 changes how RemSound describes audio frame sizes
        to other RemSound machines on the wire. Two v3.0
        machines talk to each other perfectly. A v3.0 machine
        talking to a v2.x machine will still pass audio, but
        the v2.x side will build up too much buffer and
        latency will be very high. To avoid this, update BOTH
        machines to v3.0. The auto-updater on v1.9 and later
        will handle the upgrade for you, but the timing
        matters: if one machine updates before the other,
        expect a short period of high latency until the
        second machine catches up.

        If you had previously ticked "do not show me this
        message again" on the v2.x "save was blocked on a
        read-only profile" dialog, that suppression doesn't
        carry over — you'll see the new one-time warning once
        per machine. That's deliberate; the behaviour changed
        and you need to know.

        RemSound v2.2

        A maintenance release that makes RemSound use less of
        your computer's CPU and memory, especially when sending
        with the Opus codec. No new features to learn, no
        settings have changed, and audio sounds exactly the same.
        Wire format and audio pipeline are unchanged — every
        version from v1.5 to v2.2 still talks to every other
        version cleanly.

        What's lighter on your computer:
          * Opus sending uses much less memory. RemSound's
            Opus encoder used to put quite a lot of work on
            Windows' memory manager — about 4 megabytes per
            second of "throwaway" memory churn while sending
            Opus audio. v2.2 ships a native build of the same
            encoder that does its work in a tighter, faster way.
            The audio you hear is identical (it really is the
            same encoder, just packaged better); the memory
            churn drops by about 97 per cent. On laptops you
            should see less background CPU when streaming Opus,
            and longer sessions are less likely to see brief
            pauses while Windows tidies up memory.
          * Smaller all-round efficiency tidy-up. A handful of
            small fixes — RemSound checks the audio-device list
            less often, reuses some small bits of memory it
            used to make fresh each time, and skips some
            paperwork on the receive side when there's nothing
            to do. Each one is small on its own; together they
            cut RemSound's everyday memory churn modestly.
          * Removed some old leftover code that was retired
            months ago but still lived on as zero-valued
            columns in the diagnostic log. Same behaviour,
            cleaner files.

        For people who use the diagnostic logs:
          * Several new columns. "cpu" shows how much of one
            CPU core RemSound just used. "memMB" and "wsMB" are
            its memory footprint. "allocKBps" is the per-second
            memory-churn rate. "captureMs / sendMs / recvMs /
            renderMs" show how busy each of the four audio
            threads is. All of this only writes to the log when
            Enable logs is ticked; with logs off it costs
            nothing.
          * "fanCacheMs", "driftDrop", "driftRep" and "driftAcc"
            columns have been removed — they were always zero
            after the playback engine changed in May.

        No bug fixes in v2.2 specifically — everything carried
        over from v2.1's UPnP, lock-profile, wake-from-sleep
        and hibernate fixes is still in place.

        What's new:
          * Automatic router port opening (UPnP). RemSound can
            now ask your router to let peers on the internet
            reach you, so you don't have to set up port
            forwarding by hand. Off by default. Tick the new
            "Automatically open my router for incoming
            connections (UPnP)" box in Preferences to turn it
            on. A status line right below the tick tells you
            what happened — found your router and opened the
            port, found your router but the port couldn't be
            opened, or no router found that supports this
            feature. If your internet provider puts you behind
            a second layer of NAT (common on mobile broadband
            and some cable connections), the status line will
            say so and suggest using Tailscale or the relay
            instead.
          * Check for updates on startup. New checkbox in
            Preferences, on by default. Shortly after RemSound
            launches it has a quiet look for a new release.
            Combined with "Silently install updates", this
            means leaving RemSound to keep itself up to date
            without you ever needing to think about it.
          * Brief notice before a silent update installs. When
            RemSound finds an update at startup and is set to
            install silently, it now shows a small window with
            the version it's about to install and an 8-second
            countdown. Press Enter (or wait) to install now,
            "Skip this version" to leave the update for
            another day, or "Postpone" to try again at the
            next check. Without this notice, the app could
            silently close on you a few seconds after launch
            and you'd have no idea why.
          * Lock profile (read-only). New tickable item in the
            File menu (Alt+F, L). When ticked, anything you
            change while RemSound is running stays in this
            session and is forgotten on close — your saved
            profile is left untouched, and there's NO "save
            changes?" prompt on exit. Useful when you have a
            default profile you want to keep clean even if you
            toggle send/receive or volume during the day, and
            essential if a save prompt could block shutdown
            when you can't reach it (screen reader gone,
            remote session dropped, machine hibernating).
            Saved per profile, off by default, toggle as often
            as you like. Save As on a locked profile produces
            an unlocked copy you can edit normally.
          * "Cue sounds" in Preferences is now labelled "Audio
            cue sounds" for clarity.

        Bug fixes:
          * No sound after the computer wakes from sleep. On
            many setups (especially USB audio interfaces),
            waking the computer left RemSound's audio engine
            in a state where it looked like it was running but
            no sound actually came out — you'd have to quit
            and reopen RemSound to get audio back. RemSound
            now notices when the system has woken up, waits a
            moment for the USB devices to settle, and rebuilds
            its audio engine automatically. The "Loading audio
            driver" window briefly appears during the rebuild
            so you can see it's happening.
          * Receiver audio silent after waking from hibernate.
            A follow-up to the wake-from-sleep fix above: on
            hibernate (rather than ordinary sleep) the ASIO
            receive output's tick selection could be silently
            wiped during hibernation entry, leaving the
            receiver running silent on resume even though
            everything looked normal in the logs. Fixed by
            recognising the transient driver-disappeared state
            and preserving the user's tick until the driver
            comes back.

        RemSound v2.0

        A smoother startup when a profile uses an ASIO driver.
        No wire-format or audio-pipeline changes — v1.5 through
        v2.0 peers interoperate.

        Change:
          * Opening an ASIO driver takes a couple of seconds,
            and during that time the main window used to look
            frozen on startup. RemSound now shows a small
            "Loading audio driver" window while the driver
            opens, so startup no longer looks hung. The window
            then opens as normal. Profiles that don't use an
            ASIO driver are unaffected — they still start
            instantly, with no extra window.

        RemSound v1.9

        Critical auto-updater fix. No wire-format or
        audio-pipeline changes — v1.5 through v1.9 peers
        interoperate.

        Bug fix:
          * The auto-updater now actually installs updates.
            Every previous version had a fault in the update
            step: the folder path handed to the file-copy
            command ended in a backslash, which Windows
            mis-read, so the copy was rejected and no files
            were ever replaced. Check for updates would
            download the new version but never apply it. That
            copy step is fixed.

        Because the fault was in the OLD version doing the
        updating, v1.8 and earlier cannot install v1.9 for you
        — install v1.9 by hand once (download the zip, extract
        it over your RemSound folder). From v1.9 onward, Check
        for updates installs every update automatically.

        RemSound v1.8

        Updater polish and a rewritten user manual. No
        wire-format or audio-pipeline changes — v1.5 through
        v1.8 peers interoperate.

        Changes:
          * Check for updates looks further down the release
            list, so it reliably finds the newest RemSound
            version.
          * An update can no longer overwrite your own
            settings, profiles, logs or recordings — it only
            replaces program files.
          * After a successful update the install folder is
            left tidy: temporary update files, the update log
            and any old failure note are cleared automatically.
          * If an update ever fails, the note it leaves
            (update-failed.txt) is now written in plain
            language with clear steps to follow.
          * The user manual (press F1) has been rewritten in
            plain language throughout.

        RemSound v1.7

        Updater fix. No wire-format or audio-pipeline changes —
        v1.5, v1.6 and v1.7 peers interoperate.

        Bug fix:
          * Check for updates now reliably finds RemSound
            releases. RemSound and the relay server share one
            GitHub repository; the server's releases use
            "server-" tags. The updater previously looked only
            at the single newest release of any kind, so a
            server release could derail it (it would misread
            the version and report "up to date"). It now scans
            the release list and considers only RemSound client
            versions, ignoring server releases, drafts and
            pre-releases.

        RemSound v1.6

        Three reliability fixes. No wire-format or audio-pipeline
        changes — v1.5 and v1.6 peers interoperate.

        Bug fixes:
          * Peer address recovery. If the address you connected
            to goes unreachable — a peer rebooted onto a new IP,
            or a computer name resolved to a stale address —
            RemSound now follows the peer to the live address it
            is heartbeating from, instead of sending audio into
            the void. Recovers on its own within a few seconds.
            Limited to private-network addresses so a relay can
            never be mistaken for a moved peer.
          * Fixed a crash that could happen when a peer
            reconnected (e.g. after rebooting). The Connectivity
            peer list could be read mid-rebuild with a stale
            index and bring the app down from the status timer.
          * Fixed runaway memory and CPU on a long-running
            receiver. Decoder sessions orphaned by peer
            reconnects were not being reclaimed — over hours they
            piled up, each holding a multi-megabyte buffer and
            costing render-thread time every callback. They are
            now reaped once idle, with a hard cap as a backstop.

        RemSound v1.5

        Menu reorganisation, multi-peer audio-routing fix, recording
        fix for BothIndependent mode, and Ctrl+O for Open profile.
        Wire format and audio pipeline are unchanged — v1.4 and v1.5
        peers interoperate.

        Bug fixes:
          * BothIndependent recording: when both WASAPI and ASIO
            output devices were ticked, recordings came out garbled
            and twice the expected duration. The recorder taps fired
            from both lanes' render reads and the writer thread
            appended both streams into one ring as if they were
            sequential audio. Recorder now has per-lane rings and
            mixes them in the writer thread.
          * A peer announcing audio on the WASAPI lane was inaudible
            when the receiver only had an ASIO output device ticked
            (and vice versa). Sessions whose announced lane has no
            active output now fall through to whichever lane IS
            being read.

        Menu reorganisation:
          * New Options menu (Alt+O) holds: Recording settings,
            Keyboard shortcuts, Startup behaviour, Preferences.
            These used to be scattered across File menu (Keyboard
            shortcuts, Preferences), Record menu (Recording settings),
            and inside the Preferences dialog (Startup behaviour).
          * Record menu mnemonic moved from Alt+O to Alt+K (rendered
            as "Record (Alt+K)") so Alt+O could go to Options. K is
            unusual for "Record" but the Record menu doesn't have a
            natural free letter — Alt+R is taken by Receive audio.
          * File menu: new Recent profiles submenu (Alt+F, R). Lists
            the last five profiles you've opened, most-recent first.
            Press 1..5 while the submenu is open to jump to a slot.
            File → Rename current profile moves to Alt+F, M, and
            Minimise to tray moves to Alt+F, N, to free up R for the
            new submenu.
          * Lock to audio clock (Audio profile tab) was Alt+K; now
            Alt+D (the D in "audio") since the Record menu won Alt+K.

        UX additions:
          * Ctrl+O opens the Open profile dialog (matches the menu
            chord). Previously the menu had no global shortcut.
          * New global hotkey: Start / Stop recording. Pickable from
            Options - Keyboard shortcuts. Unbound by default. Works
            system-wide — RemSound doesn't need keyboard focus.

        Recording feature unchanged in this release — the dialog
        layout, formats (WAV / MP3 / OGG-Opus / FLAC), and tap-points
        all the same as v1.4.

        RemSound v1.4

        Recording-settings dialog cleanup and a few mnemonic
        adjustments. No wire-format or audio-pipeline changes — v1.4
        and v1.3 peers interoperate.

        Recording settings dialog:
          * Channel mode is now its own dedicated listbox (Alt+C)
            instead of being folded into every attribute row. WAV
            shrinks from 6 attribute rows to 3, MP3 and OGG-Opus from
            8 to 4, FLAC from 4 to 2.
          * FLAC compression level (0..8) is now selectable in its
            own listbox (Alt+L). Previously hard-fixed at the libFLAC
            default of 5. The list is shown only when the file format
            is FLAC. Friendly tags on the endpoints: "0 — fastest
            encode, biggest file", "5 — default (libFLAC reference)",
            "8 — slowest encode, smallest file". All levels produce
            bit-identical audio — pure encode-time vs file-size
            tradeoff.

        Record menu mnemonics:
          * Start / Stop recording is now Alt+O, R (was Alt+O, S).
            Matches the Ctrl+R global toggle so the same letter does
            the same job from either entry point.
          * Recording settings is now Alt+O, S (was Alt+O, T). Reads
            more naturally now that the R slot is freed.
          * Open folder (O) and Change folder (C) unchanged.

        Dialog mnemonic adjustment:
          * Cancel button in the Recording settings dialog moved
            from Alt+C to Alt+N so the Channels listbox can take
            Alt+C. Esc still dismisses the dialog the way it always
            has.

        RemSound v1.3

        Updater hardening. The v1.2 self-updater silently failed on
        installs inside Dropbox-synced folders because Dropbox held
        write locks on the existing files during the brief window
        between the parent exiting and the helper script copying the
        new files in. Robocopy gave up after 5 seconds and the helper
        relaunched the old binary anyway — so the user saw the same
        version they started with after pressing "Yes" on the install
        prompt.

        What's new:
          * Helper script retries robocopy for up to 60 seconds per
            file (was 5). Dropbox lock release happens reliably within
            that window in practice.
          * Helper now checks robocopy's exit code. On a true failure
            (exit code 8 or higher), it writes update-failed.txt next
            to RemSound.exe with diagnostic detail and does NOT
            relaunch the old binary. Earlier versions silently
            relaunched the unmodified old binary, hiding the failure.
          * Helper writes a step-by-step log to _update-helper.log in
            the install folder. If a future update goes wrong this is
            where the trace lives.
          * Stale failure markers are cleared at the start of every
            new update attempt, so a successful run leaves the install
            folder clean.

        Wire format, audio pipeline, and recording feature are
        unchanged from v1.2.

        RemSound v1.2

        Recording, sound cues, and receiver-side drift compensation.
        This release is mostly about features that sit on top of the
        v1.1 transport — the wire format and audio pipeline are
        unchanged, so v1.1 and v1.2 peers interoperate.

        What's new:
          * Recording. New Record menu (Alt+O) — Start / Stop with
            Ctrl+R, dedicated settings dialog, per-profile choice of
            source (received only, sent only, or both), file format
            (WAV, MP3, OGG-Opus, FLAC), bit depth or bitrate, mono or
            stereo, and recordings folder. Files are crash-resilient:
            WAV re-patches its RIFF header every 5 seconds, MP3 / FLAC
            / OGG-Opus all produce well-formed truncated files if the
            app crashes mid-recording.
          * OGG-Opus and FLAC encoders now wired up — they were stubs
            in earlier builds. OGG-Opus reuses the same Concentus
            encoder as the wire path; FLAC uses pure-managed CUETools
            FLAKE (no native DLL).
          * Recording start / stop sound cues. Plays a short ding
            when recording transitions on or off. Played via the
            default Windows output device, separate from the
            recording pipeline, so a normal recording does not include
            the cue.
          * Sound-cue Preferences. The old single "Mute connect /
            disconnect sounds" checkbox is replaced by a per-cue
            CheckedListBox: Connect / Disconnect / Recording start /
            Recording stop, each independently toggleable. Old profile
            settings that had the legacy mute on are honoured on first
            load.
          * Receiver-side drift compensation switched from discrete
            single-frame splices to a continuous WdlResampler running
            at a slowly-updated rate ratio. Smooths out long-session
            clock drift between sender and receiver without the
            occasional 21 µs splice the v1.1 corrector emitted.

        UI changes:
          * Record menu moved to Alt+O (Rec&ord). The old Alt+R chord
            conflicted with the Receive audio checkbox on the main
            form. Inside the menu the item mnemonics are unchanged
            (S / T / O / C for Start, settings, Open folder, Change
            folder).
          * Auto-tune interval combo label and accessible name are now
            mode-aware. In BothIndependent mode it reads "Auto-tune
            interval — WASAPI and ASIO" so it's clear the same combo
            drives ticks for both lanes; each lane still independently
            tunes to its own target latency. Earlier builds also had
            a bug where ticking ASIO auto-tune alone left this combo
            greyed out — fixed.

        Diagnostics (only active with Enable logs ticked):
          * Per-stage discontinuity probes — sender raw capture,
            sender pre-encode (now per lane in BothIndependent),
            receiver post-decode, receiver post-ring, receiver
            post-resampler. Lets a log inspection localise where a
            click was introduced (capture / wire / decode / playout).
          * Wire-level sequence tracking on each PCM stream:
            in-order / missed / reordered / duplicated packet counts
            in the diag log. Healthy LAN should show all-zero except
            in-order; non-zero on the others points to transport
            issues rather than software.
          * Clipped-sample delta in the diag log.

        Bug fixes:
          * Auto-tune interval combo no longer greys out when only
            ASIO auto-tune is ticked in BothIndependent.

        RemSound v1.1

        Priority and performance hardening, plus always-on network
        packet priority. Aimed at the cold-start latency jitter you
        sometimes hear when the machine has been idle.

        What's new:
          * Use CPU and Windows performance settings in high priority
            mode (Audio profile tab, Alt+U). Off by default; opt in per
            profile. Combines eight Windows power and scheduling levers
            so the OS can't decide we're idle and downclock: process
            power-throttling off (including timer-resolution honouring),
            an execution-required power request, scheduler quantum
            pinned to 1 ms, process priority raised to High, working-set
            minimum locked, memory priority normalised, and
            SetThreadExecutionState set to keep the system awake. Fully
            reversed on toggle-off and on app exit. Costs battery on
            laptops — that's why it's off by default.
          * Network packet priority — always on, no toggle. The
            outbound UDP socket is attached to Windows' built-in qWAVE
            service at Voice priority (DSCP 46, WMM Voice on Wi-Fi). On
            a busy Wi-Fi access point, RemSound's packets win
            medium-contention against other clients; on wired LAN,
            switches that honour DSCP give us scheduling priority.
            Neutral across the public internet (most ISPs strip DSCP at
            the edge). Silent fallback if the qWAVE service isn't
            available on stripped-down Windows images.
          * Sender and receiver UDP kernel buffers bumped to 1 MB each
            way — enough to absorb roughly 30 ms of GC or scheduler
            stall without dropping datagrams.
          * MMCSS audio-thread priority on the network listener, mix
            loop, and render thread bumped from High to Critical
            (always on, no toggle).

        Bug fixes:
          * Toggling "Enable logs" in Preferences no longer marks the
            current profile as dirty. Logs are a machine-local setting,
            so the spurious flag was triggering the "unsaved changes?"
            prompt on exit after just toggling logs.

        RemSound v1.0

        Initial public release.

        Highlights:
          * Low-latency peer-to-peer audio over UDP. WASAPI for any Windows audio device,
            and a parallel ASIO lane for pro audio interfaces (Audient, Komplete Audio,
            Focusrite, RME). Each lane keeps its own native callback latency.
          * Pick an ASIO driver from the dropdown at the top of the Audio inputs and outputs
            tab to bring ASIO into the pipeline; select "(none)" to run WASAPI-only.
          * Profile system. Save your entire setup — device ticks, peers, codec, latency
            targets, hotkeys, ASIO driver choice — into a JSON file. Pick which profile to
            load at every launch.
          * Continuous auto-tune on either lane. Watches receive jitter and nudges the
            latency target up or down to stay click-free without forcing you to overshoot.
          * Opus inband FEC. Single-packet losses recover transparently in both Opus modes;
            you don't hear them at all. PCM is also available for clean LAN connections.
          * Remote control. Configurable global hotkeys can nudge a peer's RemSound volume
            or their Windows default-output-device master volume, opt-in on the receiver.
          * Built-in self-updater. Optionally polls GitHub for newer releases on a schedule
            you set; can install them silently if you want.

        See the user manual (Help menu, or F1 from anywhere in the app) for full details
        on every control, the keyboard shortcuts, and the troubleshooting guide.
        """;

    public AboutDialog()
    {
        Text = "About RemSound";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(560, 420);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        var headingLabel = new Label
        {
            Text = $"RemSound version {version}",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold),
            AccessibleName = $"RemSound version {version}",
        };

        var notesBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            TabStop = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Text = ReleaseNotes,
            AccessibleName = "Release notes (tab into and arrow to read)",
        };

        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            TabIndex = 1,
        };
        closeButton.Click += (_, _) => Close();
        notesBox.TabIndex = 0;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.Add(closeButton);

        root.Controls.Add(headingLabel, 0, 0);
        root.Controls.Add(notesBox, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };
    }
}
