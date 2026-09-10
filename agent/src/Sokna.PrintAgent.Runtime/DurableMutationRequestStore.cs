using System.Text.Json;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed class DurableMutationRequestStore
{
    private const string LegacyAgentVersion="6.2.0";
    private readonly LocalQueueStore _store;

    public DurableMutationRequestStore(LocalQueueStore store)=>_store=store;

    public async Task<AcceptRequestEnvelope> GetOrCreateAcceptAsync(LocalJob job,CancellationToken ct)
    {
        var key=AcceptKey(job.AttemptId);
        var existing=await ReadAsync<AcceptRequestEnvelope>(key,ct);
        if(existing is not null)
        {
            ValidateAccept(existing,job);
            return existing;
        }

        var legacyRequestId=await _store.GetMetaAsync($"accept_request:{job.AttemptId}",ct);
        var created=new AcceptRequestEnvelope(
            string.IsNullOrWhiteSpace(legacyRequestId)?CryptoUtil.NewRequestId():legacyRequestId,
            string.IsNullOrWhiteSpace(legacyRequestId)?AgentVersionInfo.Current:LegacyAgentVersion,
            4,
            job.AttemptId,
            job.LocalReceiptId,
            job.ContentSha256);
        await PersistAsync(key,created,ct);
        if(!string.IsNullOrWhiteSpace(legacyRequestId))await _store.DeleteMetaAsync($"accept_request:{job.AttemptId}",ct);
        return created;
    }

    public async Task<StartRequestEnvelope> GetOrCreateStartAsync(LocalJob job,CancellationToken ct)
    {
        var key=StartKey(job.AttemptId);
        var existing=await ReadAsync<StartRequestEnvelope>(key,ct);
        if(existing is not null)
        {
            ValidateStart(existing,job);
            return existing;
        }

        var legacyRequestId=await _store.GetMetaAsync($"start_request:{job.AttemptId}",ct);
        var created=new StartRequestEnvelope(
            string.IsNullOrWhiteSpace(legacyRequestId)?CryptoUtil.NewRequestId():legacyRequestId,
            string.IsNullOrWhiteSpace(legacyRequestId)?AgentVersionInfo.Current:LegacyAgentVersion,
            4,
            job.AttemptId);
        await PersistAsync(key,created,ct);
        if(!string.IsNullOrWhiteSpace(legacyRequestId))await _store.DeleteMetaAsync($"start_request:{job.AttemptId}",ct);
        return created;
    }

    public async Task<RenewRequestEnvelope> GetOrCreateRenewAsync(LocalJob job,CancellationToken ct)
    {
        var key=RenewKey(job.AttemptId);
        var existing=await ReadAsync<RenewRequestEnvelope>(key,ct);
        if(existing is not null)
        {
            if(existing.AttemptId!=job.AttemptId||existing.ProtocolVersion!=4||string.IsNullOrWhiteSpace(existing.RequestId)||string.IsNullOrWhiteSpace(existing.AgentVersion))
                throw new InvalidDataException("Durable renew request identity با Attempt محلی تطابق ندارد.");
            return existing;
        }
        var created=new RenewRequestEnvelope(CryptoUtil.NewRequestId(),AgentVersionInfo.Current,4,job.AttemptId);
        await PersistAsync(key,created,ct);
        return created;
    }

    public Task CompleteAcceptAsync(long attemptId,CancellationToken ct)=>_store.DeleteMetaAsync(AcceptKey(attemptId),ct);
    public Task CompleteStartAsync(long attemptId,CancellationToken ct)=>_store.DeleteMetaAsync(StartKey(attemptId),ct);
    public Task CompleteRenewAsync(long attemptId,CancellationToken ct)=>_store.DeleteMetaAsync(RenewKey(attemptId),ct);

    private async Task<T?> ReadAsync<T>(string key,CancellationToken ct)
    {
        var raw=await _store.GetMetaAsync(key,ct);
        if(string.IsNullOrWhiteSpace(raw))return default;
        try{return JsonSerializer.Deserialize<T>(raw,AgentOptions.JsonOptions())??throw new InvalidDataException($"Durable request metadata {key} خالی است.");}
        catch(JsonException e){throw new InvalidDataException($"Durable request metadata {key} JSON نامعتبر است.",e);}
    }

    private async Task PersistAsync<T>(string key,T value,CancellationToken ct)
        => await _store.SetMetaAsync(key,JsonSerializer.Serialize(value,AgentOptions.JsonOptions()),ct);

    private static void ValidateAccept(AcceptRequestEnvelope request,LocalJob job)
    {
        if(request.AttemptId!=job.AttemptId||request.ProtocolVersion!=4||
           !string.Equals(request.LocalReceiptId,job.LocalReceiptId,StringComparison.Ordinal)||
           !string.Equals(request.ContentSha256,job.ContentSha256,StringComparison.OrdinalIgnoreCase)||
           string.IsNullOrWhiteSpace(request.RequestId)||string.IsNullOrWhiteSpace(request.AgentVersion))
            throw new InvalidDataException("Durable accept request identity/body با Attempt محلی تطابق ندارد.");
    }

    private static void ValidateStart(StartRequestEnvelope request,LocalJob job)
    {
        if(request.AttemptId!=job.AttemptId||request.ProtocolVersion!=4||string.IsNullOrWhiteSpace(request.RequestId)||string.IsNullOrWhiteSpace(request.AgentVersion))
            throw new InvalidDataException("Durable start request identity/body با Attempt محلی تطابق ندارد.");
    }

    private static string AcceptKey(long attemptId)=>$"accept_request_v2:{attemptId}";
    private static string StartKey(long attemptId)=>$"start_request_v2:{attemptId}";
    private static string RenewKey(long attemptId)=>$"renew_request_v2:{attemptId}";
}
