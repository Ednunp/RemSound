using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// "Send my audio" governs the person's own inputs, and a DAW track sending does not change that (2026-09-25 sweep, Ed:
/// "yes fix that"). A sending plugin used to count as wanting to send, which started the capture with every ticked input
/// in it - so the microphone went out with "Send my audio" off, and unticking it while a track was sending kept the
/// microphone going, while the peer list said "not sending".
///
/// <para>Driven through the real window in each of the three audio configurations (they decide which lane the DAW track
/// travels on), connected on a throwaway port with its one peer a listener of this step's own, and a real plugin link on
/// a port the OS picks. The ticked source is the loopback of Windows' default output - a real capture that makes no
/// sound and goes nowhere but the step's own listener on this computer. The ASIO configurations use the gate's
/// made-up driver, so no interface is opened.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? SendOffKeepsYourOwnInputsOffWhileAPluginSends()
    {
        string defaultOutputId;
        try
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            defaultOutputId = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).ID;
        }
        catch (Exception ex) { return Skip($"this computer has no default output to capture from ({ex.GetType().Name})"); }

        var linkBefore = AppConfig.Load().EnableDawPluginLink;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var covered = new List<string>();
        try
        {
            { var cfg = AppConfig.Load(); cfg.EnableDawPluginLink = true; cfg.Save(); }
            foreach (var configuration in AudioConfigurations.All)
            {
                var name = configuration.Describe();
                var peerSink = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int windowPort;
                using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                    windowPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
                MainForm.LocalAudioPortForTest = windowPort;
                MainForm? form = null;
                var lines = new List<string>();
                var feeding = new CancellationTokenSource();
                Task? feeder = null;
                try
                {
                    var profile = Profile.NewBlank();
                    profile.Password = RemSoundCrypto.Obfuscate("send-off-plugin-password");
                    try { form = new MainForm(null, profile, null, null, headless: true); }
                    catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                    _ = form.Handle;
                    var main = form;
                    main.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
                    SetAudioConfigurationForTest(main, configuration);
                    var sinkPort = ((IPEndPoint)peerSink.Client.LocalEndPoint!).Port;
                    main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Send-off peer", sinkPort, true, true, DateTime.UtcNow, IPAddress.Loopback));
                    var sendBox = Require(FieldOf<CheckBox>(main, "sendMyAudioCheckbox"), "MainForm.sendMyAudioCheckbox not found");
                    sendBox.Checked = false;
                    // A source of your own, ticked: what the bug put on the wire.
                    var outputs = Require(FieldOf<CheckedListBox>(main, "sendOutputDevicesList"), "MainForm.sendOutputDevicesList not found");
                    var index = outputs.Items.Add(new AudioDeviceChoice("Gate's own loopback", defaultOutputId, CaptureKind.Loopback));
                    outputs.SetItemChecked(index, true);
                    Application.DoEvents();

                    Require(typeof(MainForm).GetMethod("Connect", flags, Type.EmptyTypes), "MainForm.Connect not found").Invoke(main, null);
                    var ensure = Require(typeof(MainForm).GetMethod("EnsureRequestedAudioRunning", flags), "MainForm.EnsureRequestedAudioRunning not found");
                    var apply = Require(typeof(MainForm).GetMethod("ApplyAudioRuntime", flags), "MainForm.ApplyAudioRuntime not found");
                    var sender = main.SenderForTest;
                    Check(!sender.IsRunning && sender.PendingSourceCountForTest == 0, $"{name}: premise: with \"Send my audio\" off and no plugin, nothing of your own is captured");

                    // A DAW track starts sending, and keeps sending while the checks run - as a playing track does.
                    var host = Require(main.OpenPluginLinkForTest(0), "the plugin link must open on the step's own port");
                    using var plugin = new PluginBridgeClient(host.Port);
                    plugin.Hello();
                    var track = new float[256 * PluginBridgeProtocol.WireChannels];
                    for (var i = 0; i < track.Length; i++) track[i] = 0.1f;
                    feeder = Task.Run(() =>
                    {
                        while (!feeding.IsCancellationRequested) { plugin.SendTrackBlock(track); Thread.Sleep(5); }
                    });
                    Check(WaitUntil(() => main.PluginTrackSourceForTest.AnyHostSending && sender.IsPluginSending, 3000), $"{name}: premise: the DAW track must be arriving, its lane armed");

                    // 1. Off, and a track sending: the once-a-second check and the set-up leave your own sources alone.
                    _ = sender.TakeSenderAudioFramesSent();
                    for (var i = 0; i < 3; i++) { ensure.Invoke(main, null); apply.Invoke(main, null); Application.DoEvents(); Thread.Sleep(100); }
                    Check(!sender.IsRunning && sender.PendingSourceCountForTest == 0,
                        $"{name}: THE MICROPHONE: with \"Send my audio\" off, a sending DAW track must not start your own capture (running: {sender.IsRunning}, sources handed over: {sender.PendingSourceCountForTest})");
                    Check(WaitUntil(() => sender.TakeSenderAudioFramesSent() > 0, 3000), $"{name}: and the DAW track itself must still go out");

                    // 2. On: your own source is captured beside the track - so the check above could fail.
                    sendBox.Checked = true;
                    apply.Invoke(main, null);
                    Application.DoEvents();
                    Check(sender.PendingSourceCountForTest == 1 && sender.IsRunning,
                        $"{name}: premise: with \"Send my audio\" on, your ticked source must be captured (running: {sender.IsRunning}, sources: {sender.PendingSourceCountForTest})");
                    var streamBefore = sender.PluginLaneStreamIdForTest;

                    // 3. Off again while the track plays: your source stops; the track's lane carries on, same stream, no gap.
                    lock (lines) lines.Clear();
                    sendBox.Checked = false;
                    apply.Invoke(main, null);
                    Application.DoEvents();
                    Check(!sender.IsRunning && sender.PendingSourceCountForTest == 0,
                        $"{name}: THE MICROPHONE: unticking \"Send my audio\" while a track sends must stop your own capture (running: {sender.IsRunning}, sources: {sender.PendingSourceCountForTest})");
                    Check(sender.IsPluginSending && sender.PluginLaneStreamIdForTest == streamBefore,
                        $"{name}: the DAW track's lane must carry on as it was - still armed, the same stream, so the other end hears no gap");
                    _ = sender.TakeSenderAudioFramesSent();
                    Check(WaitUntil(() => sender.TakeSenderAudioFramesSent() > 0, 3000), $"{name}: and the track must still be going out");
                    for (var i = 0; i < 3; i++) { ensure.Invoke(main, null); Application.DoEvents(); Thread.Sleep(100); }
                    Check(!sender.IsRunning && sender.PendingSourceCountForTest == 0, $"{name}: and the once-a-second check must not start your capture again");
                    // The device changes, the send-mode switch and the wake all hand the sources over through one place; with
                    // the box off it must hand over none, whatever else has decided to call it.
                    Require(typeof(MainForm).GetMethod("ApplySendSources", flags), "MainForm.ApplySendSources not found").Invoke(main, null);
                    Check(sender.PendingSourceCountForTest == 0, $"{name}: THE ONE PLACE: with \"Send my audio\" off, handing the sources over must hand over none");
                    lock (lines)
                        Check(lines.Any(l => l.Contains("your own capture stopped (\"Send my audio\" is off) - the DAW track goes on sending", StringComparison.Ordinal)),
                            $"{name}: THE LOG: stopping your own capture while the track goes on must be logged");
                    covered.Add(name);
                }
                finally
                {
                    feeding.Cancel();
                    try { feeder?.Wait(1000); } catch { /* teardown */ }
                    try { if (form is not null) form.LogForTest.EventTapForTest = null; } catch { /* teardown */ }
                    try { form?.Dispose(); } catch { /* teardown */ }
                    MainForm.LocalAudioPortForTest = 0;
                    peerSink.Dispose();
                }
            }
        }
        finally
        {
            var cfg = AppConfig.Load(); cfg.EnableDawPluginLink = linkBefore; cfg.Save();
        }
        return $"in {string.Join(", ", covered)}: with \"Send my audio\" off and a DAW track sending, your ticked source was never "
             + "captured and the track still went out; ticked on, the source was captured; ticked off again mid-track, the source "
             + "stopped, the track's lane carried on on the same stream, the once-a-second check left it off, the one place every "
             + "path hands sources over handed over none, and the log said so";
    }
}
