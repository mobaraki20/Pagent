namespace Sokna.PrintAgent.Service;

public sealed record BridgeHeartbeatFields(
    int ProtocolVersion,
    int Port,
    string? PairingId,
    string? Origin);

/// <summary>
/// Projects process-local listener truth into heartbeat wire fields. Config intent is deliberately
/// ignored: a configured but failed/stopped listener must never advertise a usable local bridge.
/// </summary>
public static class BridgeHeartbeatProjection
{
    public static BridgeHeartbeatFields From(BridgeRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if(!snapshot.Listening||!snapshot.Enabled||snapshot.Port<=0||string.IsNullOrWhiteSpace(snapshot.Origin)||string.IsNullOrWhiteSpace(snapshot.PairingId))
            return new(0,0,null,null);
        return new(1,snapshot.Port,snapshot.PairingId,snapshot.Origin);
    }
}
