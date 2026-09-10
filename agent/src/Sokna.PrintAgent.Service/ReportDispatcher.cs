using System.Text.Json;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed record ReportDispatchSummary(
    int Attempted,
    int Delivered,
    int Backoff,
    int AuthBlocked,
    int ReconciliationRequired,
    string? LastErrorCode,
    string? LastErrorMessage);

public sealed class ReportDispatcher
{
    private readonly LocalQueueStore _store;
    private readonly ReportDeliveryPolicy _policy;
    private readonly AgentLog _log;

    public ReportDispatcher(LocalQueueStore store,ReportDeliveryPolicy policy,AgentLog log)
    {
        _store=store;
        _policy=policy;
        _log=log;
    }

    public async Task<ReportDispatchSummary> DispatchBatchAsync(IPrintTransport transport,string activeServerScope,int limit,CancellationToken ct)
    {
        var attempted=0;
        var delivered=0;
        var backoff=0;
        var authBlocked=0;
        var reconciliation=0;
        string? lastCode=null;
        string? lastMessage=null;

        foreach(var row in await _store.PendingReportsAsync(limit,ct))
        {
            attempted++;
            var job=await _store.GetByAttemptAsync(row.AttemptId,ct);
            var outcome=await _store.GetOutcomeAsync(row.AttemptId,ct);
            if(job is null||outcome is null)
            {
                const string code="missing_local_outcome";
                await _store.MarkReportDeliveryAsync(row.Id,ReportDeliveryState.ReconciliationRequired,"Local attempt/outcome برای report پیدا نشد.",null,code,null,ct);
                reconciliation++;
                lastCode=code;
                continue;
            }

            if(!ScopeCompatible(row.ServerScope,activeServerScope))
            {
                const string code="server_scope_mismatch";
                await _store.MarkReportDeliveryAsync(row.Id,ReportDeliveryState.ReconciliationRequired,"Report متعلق به server scope دیگری است و خودکار ارسال نمی‌شود.",null,code,null,ct);
                reconciliation++;
                lastCode=code;
                continue;
            }

            ReportRequestEnvelope request;
            try
            {
                request=JsonSerializer.Deserialize<ReportRequestEnvelope>(row.BodyJson,AgentOptions.JsonOptions())
                    ?? throw new InvalidDataException("Report envelope خالی است.");
                ValidateEnvelope(job,outcome,row,request);
            }
            catch(Exception e)
            {
                const string code="invalid_durable_report_envelope";
                await _store.MarkReportDeliveryAsync(row.Id,ReportDeliveryState.ReconciliationRequired,Safe(e.Message),null,code,null,ct);
                reconciliation++;
                lastCode=code;
                lastMessage=Safe(e.Message);
                continue;
            }

            try
            {
                var result=await transport.ReportAsync(job,request,ct);
                ValidateResponse(result,job,request);
                await _store.MarkReportSentAsync(row.Id,row.AttemptId,ct);
                delivered++;
            }
            catch(Exception e)
            {
                var decision=_policy.ForException(e,row.ErrorCount,request.Status);
                if(decision.TreatAsDelivered)
                {
                    await _store.MarkReportSentAsync(row.Id,row.AttemptId,ct);
                    delivered++;
                    continue;
                }

                var api=e as PrintApiException;
                var message=Safe(api?.Message??e.Message);
                var code=api?.Code ?? decision.ReasonCode;
                await _store.MarkReportDeliveryAsync(
                    row.Id,
                    decision.State,
                    message,
                    api is null?null:(int)api.HttpStatus,
                    code,
                    decision.NextAttemptAt,
                    ct);

                lastCode=code;
                lastMessage=message;
                switch(decision.State)
                {
                    case ReportDeliveryState.Backoff: backoff++; break;
                    case ReportDeliveryState.AuthBlocked:
                        authBlocked++;
                        await _store.MarkServerScopeAuthBlockedAsync(row.ServerScope,message,api is null?null:(int)api.HttpStatus,code,ct);
                        break;
                    case ReportDeliveryState.ReconciliationRequired: reconciliation++; break;
                }

                try{_log.Warn("report_delivery",$"{decision.State}: {code ?? decision.ReasonCode}");}catch{}
                if(decision.StopCurrentServerScope)break;
            }
        }

        return new(attempted,delivered,backoff,authBlocked,reconciliation,lastCode,lastMessage);
    }

    public async Task<int> ResumeAfterCredentialProbeAsync(string activeServerScope,CancellationToken ct)
        => await _store.ResumeAuthBlockedReportsAsync(activeServerScope,ct);

    private static void ValidateEnvelope(LocalJob job,AttemptOutcome outcome,ReportOutboxRow row,ReportRequestEnvelope request)
    {
        if(request.RequestId!=row.RequestId)throw new InvalidDataException("request_id داخل body با Outbox identity تطابق ندارد.");
        if(request.AttemptId!=job.AttemptId||request.AttemptId!=outcome.AttemptId)throw new InvalidDataException("attempt_id گزارش با Outcome تطابق ندارد.");
        if(!string.Equals(request.LocalReceiptId,job.LocalReceiptId,StringComparison.Ordinal))throw new InvalidDataException("local_receipt_id گزارش ناسازگار است.");
        if(request.ProtocolVersion!=4)throw new InvalidDataException("protocol_version گزارش پشتیبانی نمی‌شود.");
        if(string.IsNullOrWhiteSpace(request.AgentVersion))throw new InvalidDataException("agent_version گزارش پایدار خالی است.");
        var expected=LocalQueueStore.ToWireStatus(outcome.Status);
        if(!string.Equals(request.Status,expected,StringComparison.Ordinal))throw new InvalidDataException("Report status با Outcome پایدار تطابق ندارد.");
        if(!string.Equals(request.SpoolerJobId??"",outcome.SpoolerJobId??"",StringComparison.Ordinal))throw new InvalidDataException("Report spooler evidence ناسازگار است.");
        if(request.Retryable!=outcome.Retryable)throw new InvalidDataException("Report retryability با Outcome پایدار ناسازگار است.");
    }

    private static void ValidateResponse(ApiResult result,LocalJob job,ReportRequestEnvelope request)
    {
        if(!result.Success)throw new InvalidDataException("Report response success=false بدون typed exception دریافت شد.");
        if(result.RequiresHumanResolution)throw new InvalidDataException("Report response نیازمند human resolution است و ACK خودکار مجاز نیست.");
        if(result.AttemptId is { } attempt&&attempt!=job.AttemptId)throw new InvalidDataException("Report response attempt_id mismatch.");
        if(result.JobId is { } serverJob&&serverJob!=job.ServerJobId)throw new InvalidDataException("Report response job_id mismatch.");
        if(!string.IsNullOrWhiteSpace(result.LocalReceiptId)&&!string.Equals(result.LocalReceiptId,job.LocalReceiptId,StringComparison.Ordinal))throw new InvalidDataException("Report response local_receipt_id mismatch.");
        if(!string.IsNullOrWhiteSpace(result.Status)&&!string.Equals(result.Status,request.Status,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Report response status با Outcome ارسالی تطابق ندارد.");
    }

    private static bool ScopeCompatible(string rowScope,string activeScope)
    {
        if(string.IsNullOrWhiteSpace(activeScope))return true;
        if(string.Equals(rowScope,"legacy-unbound",StringComparison.Ordinal))return true;
        return string.Equals(rowScope,activeScope,StringComparison.Ordinal);
    }

    private static string Safe(string value)=>SafeLogText.Sanitize(value,400);
}
