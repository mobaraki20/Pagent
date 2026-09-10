namespace Sokna.PrintAgent.Service;

public sealed record BridgeRuntimeSnapshot(
    long Generation,
    bool Enabled,
    bool Listening,
    int Port,
    string? Origin,
    string? PairingId,
    string? ErrorCode,
    string UpdatedAt);

/// <summary>
/// Process-local truth for the active bridge generation. Configuration intent is deliberately
/// separate from listening reality so heartbeat/control surfaces cannot report a dead listener as healthy.
/// </summary>
public sealed class BridgeRuntimeState
{
    private readonly object _gate=new();
    private long _generation;
    private BridgeRuntimeSnapshot _snapshot=new(0,false,false,0,null,null,null,DateTimeOffset.UtcNow.ToString("O"));

    public BridgeRuntimeSnapshot Snapshot
    {
        get{lock(_gate)return _snapshot;}
    }

    public BridgeRuntimeSnapshot MarkListening(int port,string origin,string pairingId)
    {
        lock(_gate)
        {
            _generation++;
            _snapshot=new(_generation,true,true,port,origin,pairingId,null,DateTimeOffset.UtcNow.ToString("O"));
            return _snapshot;
        }
    }

    public void MarkDisabled()
    {
        lock(_gate)_snapshot=new(_generation,false,false,0,null,null,null,DateTimeOffset.UtcNow.ToString("O"));
    }

    public void MarkStopped(bool enabled,string? errorCode=null)
    {
        lock(_gate)_snapshot=new(_generation,enabled,false,0,null,null,errorCode,DateTimeOffset.UtcNow.ToString("O"));
    }
}
