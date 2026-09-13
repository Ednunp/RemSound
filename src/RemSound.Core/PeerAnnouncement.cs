using System.Net;

namespace RemSound.Core;

public sealed record PeerAnnouncement(
    Guid InstanceId,
    string Name,
    int AudioPort,
    bool CanSend,
    bool CanReceive,
    DateTime LastSeenUtc,
    IPAddress Address);
