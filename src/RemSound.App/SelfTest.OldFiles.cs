using System.Text.Json;
using System.Text.Json.Nodes;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// OLD FILES (2026-09-28, from the review of every step): a profile and a global config as the released v5.9 and v6.0
/// wrote them, every field set to something other than its default. They were made by building each release's own
/// RemSound.Core from its git tag and serialising with that release's own settings (WriteIndented, as ProfileStore and
/// AppConfig do), so they are those releases' files, not files written to look like them. Every user upgrading has one
/// of these. A config that fails to read falls back to defaults without a word, and a renamed or retyped field loads as
/// its default - so nothing but a field-by-field comparison notices a user's settings quietly going.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Keys an old PROFILE holds that today's build no longer reads, each with why that is right. Anything else an
    /// old file holds must come back exactly as saved.</summary>
    private static readonly Dictionary<string, string> RetiredOldProfileKeys = new()
    {
        ["EnablePanForPeers"] = "merged into EnableAllPeerShaping before v5.0 shipped; left Profile 2026-09-13 (the v5.0 cut-off)",
        ["EnableEqForPeers"] = "merged into EnableAllPeerShaping before v5.0 shipped; left Profile 2026-09-13 (the v5.0 cut-off)",
        ["ReceiveMuteHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["SendMuteHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["TrayHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["VolumeUpHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["VolumeDownHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["ToggleRecordingHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["RemoteVolumeUpHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["RemoteVolumeDownHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["RemoteMuteToggleHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["SystemVolumeUpHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["SystemVolumeDownHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["SystemMuteToggleHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["QuickProfileSwitchHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
        ["SpeakStatusLineHotkey"] = "machine-wide since v4.4 - the global config holds it; the per-profile copy left 2026-09-13",
    };

    /// <summary>Keys an old GLOBAL CONFIG holds that today's build no longer reads, each with why.</summary>
    private static readonly Dictionary<string, string> RetiredOldConfigKeys = new()
    {
        ["PeerFriendlyNames"] = "the flat friendly-name list, replaced by NamedPeers; left 2026-09-13 (the v5.0 cut-off)",
        ["KeyboardShortcutsGlobalNoticeShown"] = "the one-time shortcut import's flag; left with the import 2026-09-13",
        ["KeyboardShortcutsImportOffered"] = "the one-time shortcut import's flag; left with the import 2026-09-13",
        ["LastWhatsNewVersion"] = "only told the shortcut import an upgrade from a fresh install; left with it 2026-09-13",
        ["LastUpdateCheckUtc"] = "written after every update check and read by nothing; removed in v6.0",
    };

    private static string OldFilesOpenFieldForField()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-oldfiles-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var said = new List<string>();
        var problems = new List<string>();
        foreach (var (version, profileJson, configJson) in new[] { ("v5.9", V59Profile, V59GlobalConfig), ("v6.0", V60Profile, V60GlobalConfig) })
        {
            var store = new ProfileStore();
            var title = "old profile " + version;
            File.WriteAllText(store.PathFor(title), profileJson);
            var profile = store.Load(title);
            if (profile is null) { problems.Add($"{version}: the profile did not open at all"); continue; }
            var n = CompareOldFile($"{version} profile", profileJson, JsonSerializer.Serialize(profile), RetiredOldProfileKeys, problems);

            File.WriteAllText(Path.Combine(AppConfig.UserDataDirectory, "global config.json"), configJson);
            var m = CompareOldFile($"{version} global config", configJson, JsonSerializer.Serialize(AppConfig.Load()), RetiredOldConfigKeys, problems);
            said.Add($"{version}: {n} profile fields and {m} config fields as saved");
        }
        Check(problems.Count == 0, "AN OLD FILE DID NOT OPEN AS IT WAS SAVED - a user upgrading loses these settings without a word: "
            + string.Join("; ", problems));
        // The retired lists must not rot into a place to hide a real loss: each key must be in an old file, and read by nothing today.
        foreach (var (key, _) in RetiredOldProfileKeys.Concat(RetiredOldConfigKeys))
            Check(new[] { V59Profile, V59GlobalConfig, V60Profile, V60GlobalConfig }.Any(f => f.Contains($"\"{key}\"", StringComparison.Ordinal)),
                $"the retired key {key} is in none of the old files - take it off the list");
        Check(RetiredOldProfileKeys.Keys.All(k => typeof(Profile).GetProperties().All(p => p.Name != k)),
            "a key listed as retired from the profile is still a Profile property - it must load as saved, not be excused");
        Check(RetiredOldConfigKeys.Keys.All(k => typeof(AppConfig).GetProperties().All(p => p.Name != k)),
            "a key listed as retired from the global config is still an AppConfig property - it must load as saved, not be excused");
        return string.Join("; ", said) + $"; {RetiredOldProfileKeys.Count + RetiredOldConfigKeys.Count} keys retired on purpose, each with its reason";
    }

    /// <summary>Every key of the old file, compared with the same key as today's build holds it after loading. Returns how
    /// many matched; each mismatch or missing key goes into <paramref name="problems"/>.</summary>
    private static int CompareOldFile(string what, string oldJson, string loadedJson, Dictionary<string, string> retired, List<string> problems)
    {
        var old = Require(JsonNode.Parse(oldJson)?.AsObject(), $"{what}: the old file is not JSON");
        var now = Require(JsonNode.Parse(loadedJson)?.AsObject(), $"{what}: today's build wrote no JSON");
        var matched = 0;
        foreach (var (key, value) in old)
        {
            if (retired.ContainsKey(key)) continue;
            if (!now.TryGetPropertyValue(key, out var loaded)) { problems.Add($"{what}: {key} is no longer read at all"); continue; }
            if (JsonNode.DeepEquals(value, loaded)) { matched++; continue; }
            static string Short(JsonNode? n) { var s = n?.ToJsonString() ?? "null"; return s.Length > 80 ? s[..80] + "..." : s; }
            problems.Add($"{what}: {key} was saved as {Short(value)} and opens as {Short(loaded)}");
        }
        return matched;
    }

    private const string V59Profile = """
{
  "Title": "-v59-Title",
  "ReadOnly": true,
  "Password": "PQkJfh8HAQJEHBdCFggfFlofExc=",
  "ReceiveAudioOn": true,
  "SendAudioOn": true,
  "Volume": 103,
  "Muted": true,
  "AsioDriverName": "-v59-AsioDriverName",
  "SelectedWasapiReceiveOutputs": [
    "-v59-SelectedWasapiReceiveOutputsItem"
  ],
  "SelectedAsioReceiveOutputs": [
    "-v59-SelectedAsioReceiveOutputsItem"
  ],
  "SelectedWasapiSendOutputs": [
    "-v59-SelectedWasapiSendOutputsItem"
  ],
  "SelectedWasapiSendInputs": [
    "-v59-SelectedWasapiSendInputsItem"
  ],
  "SelectedAsioSendInputs": [
    "-v59-SelectedAsioSendInputsItem"
  ],
  "WasapiSendMode": "devices-v59-WasapiSendMode",
  "SendAllApplications": false,
  "SelectedSendApplications": [
    "-v59-SelectedSendApplicationsItem"
  ],
  "AudioPort": 47833,
  "CodecRaw": 4,
  "OpusFrameMilliseconds": 483,
  "SendRateRaw": 3,
  "TightLatencyMode": false,
  "PriorityMode": true,
  "MuteConnectionCues": true,
  "EnableConnectCue": true,
  "EnableDisconnectCue": true,
  "EnableRecordStartCue": true,
  "EnableRecordStopCue": true,
  "EnableSaveCue": true,
  "EnableProfileSwitchCue": true,
  "EnableProfileMenuOpenCue": true,
  "EnableUpdateCue": true,
  "CustomCuePaths": {
    "key-v59": "-v59-CustomCuePathsValue"
  },
  "MaxLatencyMs": 83,
  "Smoothness": 6,
  "ContinuousAutoTuneEnabled": true,
  "ContinuousAutoTuneIntervalSec": 8,
  "MaxLatencyMsAsio": 13,
  "ContinuousAutoTuneAsioEnabled": true,
  "ConcealmentArtifactRaw": 5,
  "RecordingSettings": {
    "Source": 0,
    "FileFormat": 1,
    "ChannelMode": 1,
    "WavBitsPerSample": 27,
    "Mp3BitrateKbps": 323,
    "OggOpusBitrateKbps": 195,
    "FlacBitsPerSample": 27,
    "FlacCompressionLevel": 8,
    "Folder": "-v59-Folder",
    "SplitTracks": true,
    "BypassShaping": true
  },
  "RememberedPeers": [
    "-v59-RememberedPeersItem"
  ],
  "SelectedConnectedPeers": [
    "-v59-SelectedConnectedPeersItem"
  ],
  "LockPeerAddresses": true,
  "EnableAllPeerShaping": true,
  "EnablePanForPeers": true,
  "EnableEqForPeers": true,
  "PeerShaping": {
    "key-v59": {
      "Enabled": false,
      "Pan": 1.25,
      "Volume": 2.25,
      "EqMode": 1,
      "SimpleBandsDb": [
        0,
        0,
        0
      ],
      "AdvancedBandsDb": [
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0
      ],
      "ParametricBands": [
        {
          "StartHz": 1.25,
          "EndHz": 1.25,
          "GainDb": 1.25
        }
      ]
    }
  },
  "ReceiveMuteHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SendMuteHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "TrayHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "VolumeUpHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "VolumeDownHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "ToggleRecordingHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteVolumeUpHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteVolumeDownHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteMuteToggleHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemVolumeUpHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemVolumeDownHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemMuteToggleHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "QuickProfileSwitchHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SpeakStatusLineHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "AcceptRemoteVolumeCommands": true
}
""";

    private const string V59GlobalConfig = """
{
  "ProfilesDirectory": "-v59-ProfilesDirectory",
  "SaveProfileConfirmationSuppressed": true,
  "SaveOnReadOnlyWarningSuppressed": true,
  "StartMinimised": true,
  "ShowPanEqTab": false,
  "ThemeMode": "system-v59-ThemeMode",
  "MainTabOrder": [
    "-v59-MainTabOrderItem"
  ],
  "ShowDiscoveredPeers": false,
  "ShowRememberedPeers": false,
  "NamedPeers": {
    "key-v59": {
      "MachineName": "-v59-MachineName",
      "FriendlyName": "-v59-FriendlyName",
      "LastAddress": "-v59-LastAddress",
      "LastSeenUtc": "2026-09-01T12:00:00Z"
    }
  },
  "RememberedPeers": [
    "-v59-RememberedPeersItem"
  ],
  "RememberedPeersMigrated": true,
  "RememberedApplications": [
    "-v59-RememberedApplicationsItem"
  ],
  "PeerFriendlyNames": {
    "key-v59": "-v59-PeerFriendlyNamesValue"
  },
  "EnableStartupCue": false,
  "StartupCueCustomPath": "-v59-StartupCueCustomPath",
  "DefaultCueSounds": {
    "key-v59": "-v59-DefaultCueSoundsValue"
  },
  "EnableKeyboardClicks": false,
  "EnableSendOnCue": false,
  "EnableSendOffCue": false,
  "EnableReceiveOnCue": false,
  "EnableReceiveOffCue": false,
  "EnableHideCue": false,
  "EnableShowCue": false,
  "EnableCheckboxOnCue": false,
  "EnableCheckboxOffCue": false,
  "EnableTabSwitchCue": false,
  "MachineCueCustomPaths": {
    "key-v59": "-v59-MachineCueCustomPathsValue"
  },
  "LoggingEnabled": true,
  "WarnIfLogsFolderExceeds": true,
  "LogsFolderWarnThresholdMb": 103,
  "PruneOldLogs": true,
  "PruneOldLogsDays": 17,
  "ReceiveMuteHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SendMuteHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "TrayHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "VolumeUpHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "VolumeDownHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "ToggleRecordingHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteVolumeUpHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteVolumeDownHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteMuteToggleHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemVolumeUpHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemVolumeDownHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemMuteToggleHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "QuickProfileSwitchHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SpeakStatusLineHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "ToggleAllPeerShapingHotkey": {
    "Key": "M-v59-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "KeyboardShortcutsGlobalNoticeShown": true,
  "KeyboardShortcutsImportOffered": true,
  "UseDefaultOutputDevice": true,
  "UseDefaultInputDevice": true,
  "UseDefaultLoopbackSend": true,
  "StartWithProfileTitle": "-v59-StartWithProfileTitle",
  "AutoSaveNonReadOnlyMinutes": 3,
  "UpdateCheckFrequency": 0,
  "SilentlyInstallUpdates": true,
  "UpdateWindowEnabled": true,
  "UpdateWindowStartMinutes": 63,
  "UpdateWindowEndMinutes": 363,
  "CheckForUpdatesOnStartup": false,
  "ShowWhatsNewAfterUpdate": false,
  "LastWhatsNewVersion": "-v59-LastWhatsNewVersion",
  "UpnpEnabled": true,
  "LastUpdateCheckUtc": "2026-09-01T12:00:00Z",
  "RecentProfiles": [
    "-v59-RecentProfilesItem"
  ],
  "DisabledAsioDrivers": [
    "-v59-DisabledAsioDriversItem"
  ],
  "AsioDriversWarnedAbout": [
    "-v59-AsioDriversWarnedAboutItem"
  ]
}
""";

    private const string V60Profile = """
{
  "Title": "-v60-Title",
  "ReadOnly": true,
  "Password": "PQkJfh8HAQJEHBdCFggfFlofExc=",
  "ReceiveAudioOn": true,
  "SendAudioOn": true,
  "Volume": 103,
  "Muted": true,
  "AsioDriverName": "-v60-AsioDriverName",
  "SelectedWasapiReceiveOutputs": [
    "-v60-SelectedWasapiReceiveOutputsItem"
  ],
  "SelectedAsioReceiveOutputs": [
    "-v60-SelectedAsioReceiveOutputsItem"
  ],
  "SelectedWasapiSendOutputs": [
    "-v60-SelectedWasapiSendOutputsItem"
  ],
  "SelectedWasapiSendInputs": [
    "-v60-SelectedWasapiSendInputsItem"
  ],
  "SelectedAsioSendInputs": [
    "-v60-SelectedAsioSendInputsItem"
  ],
  "WasapiSendMode": "devices-v60-WasapiSendMode",
  "SendAllApplications": false,
  "SelectedSendApplications": [
    "-v60-SelectedSendApplicationsItem"
  ],
  "AudioPort": 47833,
  "CodecRaw": 4,
  "OpusFrameMilliseconds": 483,
  "SendRateRaw": 3,
  "TightLatencyMode": false,
  "PriorityMode": true,
  "MuteConnectionCues": true,
  "EnableConnectCue": true,
  "EnableDisconnectCue": true,
  "EnableRecordStartCue": true,
  "EnableRecordStopCue": true,
  "EnableSaveCue": true,
  "EnableProfileSwitchCue": true,
  "EnableProfileMenuOpenCue": true,
  "EnableUpdateCue": true,
  "CustomCuePaths": {
    "key-v60": "-v60-CustomCuePathsValue"
  },
  "MaxLatencyMs": 83,
  "Smoothness": 6,
  "ContinuousAutoTuneEnabled": true,
  "ContinuousAutoTuneIntervalSec": 8,
  "MaxLatencyMsAsio": 13,
  "ContinuousAutoTuneAsioEnabled": true,
  "ConcealmentArtifactRaw": 5,
  "RecordingSettings": {
    "Source": 0,
    "FileFormat": 1,
    "ChannelMode": 1,
    "WavBitsPerSample": 27,
    "Mp3BitrateKbps": 323,
    "OggOpusBitrateKbps": 195,
    "FlacBitsPerSample": 27,
    "FlacCompressionLevel": 8,
    "Folder": "-v60-Folder",
    "SplitTracks": true,
    "BypassShaping": true
  },
  "RememberedPeers": [
    "-v60-RememberedPeersItem"
  ],
  "SelectedConnectedPeers": [
    "-v60-SelectedConnectedPeersItem"
  ],
  "RelayServer": "-v60-RelayServer",
  "RelayConnectOnStart": true,
  "RelayTickedIds": [
    "-v60-RelayTickedIdsItem"
  ],
  "RelayPairPartnerTicked": true,
  "LockPeerAddresses": true,
  "EnableAllPeerShaping": true,
  "PeerShaping": {
    "key-v60": {
      "Enabled": false,
      "Pan": 1.25,
      "Volume": 2.25,
      "EqMode": 1,
      "SimpleBandsDb": [
        0,
        0,
        0
      ],
      "AdvancedBandsDb": [
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0
      ],
      "ParametricBands": [
        {
          "StartHz": 1.25,
          "EndHz": 1.25,
          "GainDb": 1.25
        }
      ]
    }
  },
  "AcceptRemoteVolumeCommands": true
}
""";

    private const string V60GlobalConfig = """
{
  "ProfilesDirectory": "-v60-ProfilesDirectory",
  "SaveProfileConfirmationSuppressed": true,
  "SaveOnReadOnlyWarningSuppressed": true,
  "StartMinimised": true,
  "ShowPanEqTab": false,
  "ThemeMode": "system-v60-ThemeMode",
  "MainTabOrder": [
    "-v60-MainTabOrderItem"
  ],
  "ShowDiscoveredPeers": false,
  "ShowRememberedPeers": false,
  "NamedPeers": {
    "key-v60": {
      "MachineName": "-v60-MachineName",
      "FriendlyName": "-v60-FriendlyName",
      "LastAddress": "-v60-LastAddress",
      "LastSeenUtc": "2026-09-01T12:00:00Z"
    }
  },
  "RememberedPeers": [
    "-v60-RememberedPeersItem"
  ],
  "RememberedPeersMigrated": true,
  "RememberedApplications": [
    "-v60-RememberedApplicationsItem"
  ],
  "EnableStartupCue": false,
  "StartupCueCustomPath": "-v60-StartupCueCustomPath",
  "DefaultCueSounds": {
    "key-v60": "-v60-DefaultCueSoundsValue"
  },
  "EnableKeyboardClicks": false,
  "ApplyPeerShapingToPlugin": false,
  "EnableDawPluginLink": false,
  "PluginFolder": "-v60-PluginFolder",
  "PluginFolderNoticeSuppressed": true,
  "PluginRefreshDeclinedVersion": "-v60-PluginRefreshDeclinedVersion",
  "EnableSendOnCue": false,
  "EnableSendOffCue": false,
  "EnableReceiveOnCue": false,
  "EnableReceiveOffCue": false,
  "EnableHideCue": false,
  "EnableShowCue": false,
  "EnableCheckboxOnCue": false,
  "EnableCheckboxOffCue": false,
  "EnableTabSwitchCue": false,
  "EnableHelpOpenCue": false,
  "EnableHelpCloseCue": false,
  "MachineCueCustomPaths": {
    "key-v60": "-v60-MachineCueCustomPathsValue"
  },
  "LoggingEnabled": true,
  "WarnIfLogsFolderExceeds": true,
  "LogsFolderWarnThresholdMb": 103,
  "PruneOldLogs": true,
  "PruneOldLogsDays": 17,
  "ReceiveMuteHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SendMuteHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "TrayHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "VolumeUpHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "VolumeDownHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "ToggleRecordingHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteVolumeUpHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteVolumeDownHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "RemoteMuteToggleHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemVolumeUpHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemVolumeDownHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SystemMuteToggleHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "QuickProfileSwitchHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "SpeakStatusLineHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "ToggleAllPeerShapingHotkey": {
    "Key": "M-v60-Key",
    "Control": true,
    "Shift": true,
    "Alt": true
  },
  "UseDefaultOutputDevice": true,
  "UseDefaultInputDevice": true,
  "UseDefaultLoopbackSend": true,
  "StartWithProfileTitle": "-v60-StartWithProfileTitle",
  "AutoSaveNonReadOnlyMinutes": 3,
  "AcceptPeerConnections": 1,
  "UpdateCheckFrequency": 0,
  "SilentlyInstallUpdates": true,
  "UpdateWindowEnabled": true,
  "UpdateWindowStartMinutes": 63,
  "UpdateWindowEndMinutes": 363,
  "CheckForUpdatesOnStartup": false,
  "ShowWhatsNewAfterUpdate": false,
  "ShowF1HelpMessageAtStartup": false,
  "UpnpEnabled": true,
  "RecentProfiles": [
    "-v60-RecentProfilesItem"
  ],
  "DisabledAsioDrivers": [
    "-v60-DisabledAsioDriversItem"
  ],
  "AsioDriversWarnedAbout": [
    "-v60-AsioDriversWarnedAboutItem"
  ],
  "RelayClientId": "-v60-RelayClientId",
  "RememberedRelays": [
    "-v60-RememberedRelaysItem"
  ],
  "TickedDeviceIds": [
    "-v60-TickedDeviceIdsItem"
  ],
  "RelayNoticeSuppressed": true,
  "ServiceOwnerNoticeShownFor": [
    "-v60-ServiceOwnerNoticeShownForItem"
  ]
}
""";
}
