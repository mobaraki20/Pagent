using System.Net;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed record ReportDeliveryDecision(
    ReportDeliveryState State,
    DateTimeOffset? NextAttemptAt,
    bool StopCurrentServerScope,
    bool TreatAsDelivered,
    string ReasonCode);

public sealed class ReportDeliveryPolicy
{
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;

    public ReportDeliveryPolicy(TimeProvider? clock=null,Func<double>? jitter=null)
    {
        _clock=clock ?? TimeProvider.System;
        _jitter=jitter ?? Random.Shared.NextDouble;
    }

    public ReportDeliveryDecision ForException(Exception exception,int previousErrorCount,string expectedOutcomeStatus)
    {
        if(exception is PrintApiException api)return ForApiException(api,previousErrorCount,expectedOutcomeStatus);
        if(exception is PrintProtocolException protocol)
            return new(ReportDeliveryState.ReconciliationRequired,null,false,false,protocol.Code);
        if(exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return Backoff(previousErrorCount,null,"transport_transient");
        }
        return Backoff(previousErrorCount,null,"unexpected_transient");
    }

    public ReportDeliveryDecision ForApiException(PrintApiException api,int previousErrorCount,string expectedOutcomeStatus)
    {
        var code=Normalize(api.Code);
        if(api.HttpStatus==HttpStatusCode.Unauthorized)
        {
            return new(ReportDeliveryState.AuthBlocked,null,true,false,code.Length>0?code:"http_401");
        }

        if(api.HttpStatus==HttpStatusCode.Forbidden)
        {
            if(IsAuthenticationCode(code)||IsRevocationCode(code))
            {
                return new(ReportDeliveryState.AuthBlocked,null,true,false,code.Length>0?code:"http_403_auth");
            }
            return new(ReportDeliveryState.ReconciliationRequired,null,false,false,code.Length>0?code:"http_403_policy");
        }

        if(api.HttpStatus==(HttpStatusCode)408||api.HttpStatus==(HttpStatusCode)429||(int)api.HttpStatus>=500)
        {
            return Backoff(previousErrorCount,api.RetryAfter,code.Length>0?code:$"http_{(int)api.HttpStatus}");
        }

        if(api.HttpStatus==HttpStatusCode.Conflict)
        {
            if(IsIdempotentAckCode(code)&&StateMatchesOutcome(api.CurrentState,expectedOutcomeStatus)&&!api.RequiresHumanResolution)
            {
                return new(ReportDeliveryState.Delivered,null,false,true,code);
            }
            return new(ReportDeliveryState.ReconciliationRequired,null,false,false,code.Length>0?code:"http_409_conflict");
        }

        if(api.HttpStatus==HttpStatusCode.OK)
        {
            if(IsAuthenticationCode(code)||IsRevocationCode(code))
                return new(ReportDeliveryState.AuthBlocked,null,true,false,code.Length>0?code:"business_auth_failed");
            if(IsTransientBusinessCode(code))
                return Backoff(previousErrorCount,api.RetryAfter,code.Length>0?code:"business_transient");
            return new(ReportDeliveryState.ReconciliationRequired,null,false,false,code.Length>0?code:"business_rejected");
        }

        if(api.HttpStatus is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            return new(ReportDeliveryState.ReconciliationRequired,null,false,false,code.Length>0?code:$"http_{(int)api.HttpStatus}");
        }

        if((int)api.HttpStatus>=400&&(int)api.HttpStatus<500)
        {
            return new(ReportDeliveryState.ReconciliationRequired,null,false,false,code.Length>0?code:$"http_{(int)api.HttpStatus}");
        }

        return Backoff(previousErrorCount,api.RetryAfter,code.Length>0?code:"api_transient");
    }

    private ReportDeliveryDecision Backoff(int previousErrorCount,TimeSpan? retryAfter,string reason)
    {
        var exponent=Math.Clamp(previousErrorCount,0,7);
        var baseSeconds=Math.Min(120,2*Math.Pow(2,exponent));
        var jitterFactor=0.85+Math.Clamp(_jitter(),0,1)*0.30;
        var computed=TimeSpan.FromSeconds(baseSeconds*jitterFactor);
        var requested=retryAfter is { } retry
            ? TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds,1,300))
            : TimeSpan.Zero;
        var delay=requested>computed?requested:computed;
        delay=TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds,1,300));
        return new(ReportDeliveryState.Backoff,_clock.GetUtcNow()+delay,false,false,reason);
    }

    private static bool IsAuthenticationCode(string code)
        => code is "unauthorized" or "invalid_token" or "token_invalid" or "auth_failed" or "authentication_failed" or "credential_invalid";

    private static bool IsRevocationCode(string code)
        => code is "token_revoked" or "agent_revoked" or "credential_revoked";

    private static bool IsTransientBusinessCode(string code)
        => code is "temporarily_unavailable" or "server_busy" or "retry_later" or "rate_limited";

    private static bool IsIdempotentAckCode(string code)
        => code is "already_reported" or "duplicate_report" or "idempotent_replay" or "already_applied";

    private static bool StateMatchesOutcome(string? serverState,string expectedOutcomeStatus)
    {
        if(string.IsNullOrWhiteSpace(serverState))return false;
        return string.Equals(serverState.Trim(),expectedOutcomeStatus,StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)=>string.IsNullOrWhiteSpace(value)?string.Empty:value.Trim().ToLowerInvariant();
}
