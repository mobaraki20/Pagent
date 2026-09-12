using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

internal sealed record PendingClaimState(
    int Version,
    string State,
    ClaimRequestEnvelope Request,
    string? QuarantinedAt=null,
    long? AttemptId=null,
    long? ServerJobId=null,
    string[]? ConflictFields=null,
    int ConflictCount=0,
    string? LastConflictAt=null,
    string? ServerScopeLabel=null)
{
    public const int CurrentVersion=2;
    public bool IsQuarantined=>string.Equals(State,"quarantined",StringComparison.OrdinalIgnoreCase);
    public static PendingClaimState Active(ClaimRequestEnvelope request)=>new(CurrentVersion,"active",request);
}
