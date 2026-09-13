using System.Text.Json;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Old-version carry-overs removed on 2026-09-13 (Ed's decision: everything that only served versions before 5.0).
/// What must still hold is that the settings files those versions wrote still open, with everything that still
/// means something intact.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// AN OLD PROFILE WITH RETIRED SETTINGS STILL OPENS, AND SAVING DROPS THEM.
    ///
    /// <para>2026-09-13, old-version cut-off at v5.0. The fourteen per-profile keyboard shortcuts (machine-wide since
    /// v4.4) and the separate pan and EQ switches (merged before v5.0 shipped) left Profile, along with the one-time
    /// import that read them. A profile file still carrying those keys must open with every other setting intact, and
    /// the next save must not write them back.</para>
    /// </summary>
    private static string? AuditOldProfileWithRetiredSettingsStillOpens()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-oldprofile-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var store = new ProfileStore();
        const string title = "old profile";
        var shortcut = new { Key = "M", Control = true, Shift = true, Alt = false };
        var old = new Dictionary<string, object?>
        {
            ["AcceptRemoteVolumeCommands"] = true,
            ["MaxLatencyMs"] = 57,
            ["OpusFrameMilliseconds"] = 120,
            ["EnableAllPeerShaping"] = true,
            ["EnablePanForPeers"] = true,
            ["EnableEqForPeers"] = false,
            ["ReceiveMuteHotkey"] = shortcut,
            ["SpeakStatusLineHotkey"] = shortcut,
        };
        File.WriteAllText(store.PathFor(title), JsonSerializer.Serialize(old));

        var loaded = Require(store.Load(title), "a profile file still carrying retired settings must open — it loaded as nothing");
        Check(loaded.AcceptRemoteVolumeCommands && loaded.MaxLatencyMs == 57 && loaded.OpusFrameSamplesPerChannel == 120 && loaded.EnableAllPeerShaping,
            "the settings that still mean something must come through an old profile file intact "
            + $"(remote volume {loaded.AcceptRemoteVolumeCommands}, buffer {loaded.MaxLatencyMs}, Opus frame {loaded.OpusFrameSamplesPerChannel}, "
            + $"shaping {loaded.EnableAllPeerShaping})");

        var resaved = store.PathFor(title) + ".resaved.json";
        ProfileStore.WriteProfileFile(resaved, loaded);
        var written = File.ReadAllText(resaved);
        foreach (var retired in new[] { "EnablePanForPeers", "EnableEqForPeers", "ReceiveMuteHotkey", "SpeakStatusLineHotkey" })
            Check(!written.Contains($"\"{retired}\"", StringComparison.Ordinal),
                $"saving an old profile must not write the retired key {retired} back");
        return "an old profile with retired shortcut and shaping keys opens with its settings intact, and saving drops those keys";
    }

    /// <summary>
    /// AN OLD GLOBAL CONFIG WITH RETIRED SETTINGS STILL OPENS.
    ///
    /// <para>2026-09-13, old-version cut-off at v5.0. The shortcut-import flags, the version number the import used to
    /// tell an upgrade from a fresh install, and the flat friendly-name list left AppConfig. A config file that still
    /// has them must open with every other setting intact. A config that fails to read falls back to defaults without a
    /// word, so a mistake here would quietly lose everything a user had set.</para>
    /// </summary>
    private static string? AuditOldConfigWithRetiredSettingsStillOpens()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-oldconfig-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var old = new Dictionary<string, object?>
        {
            ["UpnpEnabled"] = true,
            ["PruneOldLogsDays"] = 21,
            ["KeyboardShortcutsGlobalNoticeShown"] = true,
            ["KeyboardShortcutsImportOffered"] = false,
            ["LastWhatsNewVersion"] = "4.3.0",
            ["PeerFriendlyNames"] = new Dictionary<string, string> { ["STUDIO-PC"] = "Studio" },
        };
        File.WriteAllText(Path.Combine(AppConfig.UserDataDirectory, "global config.json"), JsonSerializer.Serialize(old));

        var cfg = AppConfig.Load();
        Check(cfg.UpnpEnabled && cfg.PruneOldLogsDays == 21,
            $"an old global config still carrying retired settings must keep everything else (UPnP {cfg.UpnpEnabled}, log days {cfg.PruneOldLogsDays}) — "
            + "a config that fails to read falls back to defaults without a word");
        return "an old global config with retired import flags, version and friendly names opens with its settings intact";
    }
}
