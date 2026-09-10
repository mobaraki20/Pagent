using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed class PrintAgentService : BackgroundService
{
    private static readonly string AgentVersion=AgentVersionInfo.Current;
    private const string PendingClaimMetaKey="pending_claim_v1";
    private const string LegacyServerScope="legacy-unbound";

    private readonly AgentPaths _paths;
    private readonly LocalQueueStore _store;
    private readonly IPrinterHealthProvider _printers;
    private readonly ILogger<PrintAgentService> _log;
    private readonly AgentLog _fileLog;
    private readonly PrintWakeSignal _wake;
    private readonly ReportDispatcher _reports;
    private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
    private readonly Mutex _mutex=new(false,@"Global\SoknaPrintAgentV6Service");

    private AgentOptions _options=new();
    private IPrintTransport? _api;
    private HttpClient? _http;
    private DateTime _configStampUtc=DateTime.MinValue;
    private DateTime _secretStampUtc=DateTime.MinValue;
    private DateTimeOffset? _lastPoll;
    private DateTimeOffset? _lastSubmission;
    private DateTimeOffset? _lastApiSuccess;
    private DateTimeOffset? _printerDiscoveryAt;
    private string? _lastSuccessfulAction;
    private string? _lastApiErrorCode;
    private int _consecutiveApiFailures;
    private long? _lastApiLatencyMs;
    private DateTimeOffset _nextHeartbeat=DateTimeOffset.MinValue;
    private DateTimeOffset _nextDestinationRefresh=DateTimeOffset.MinValue;
    private IReadOnlyList<DestinationConfig> _destinations=[];
    private string _serverScope=LegacyServerScope;

    public PrintAgentService(
        AgentPaths paths,
        LocalQueueStore store,
        IPrinterHealthProvider printers,
        ILogger<PrintAgentService> log,
        AgentLog fileLog,
        PrintWakeSignal wake,
        ReportDispatcher reports)
    {
        _paths=paths;
        _store=store;
        _printers=printers;
        _log=log;
        _fileLog=fileLog;
        _wake=wake;
        _reports=reports;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!_mutex.WaitOne(TimeSpan.Zero))throw new InvalidOperationException("نمونه دیگری از Sokna Print Agent فعال است.");
        try
        {
            await _store.InitializeAsync(stoppingToken);
            await RecoverAsync(stoppingToken);
            await WriteLocalHealthAsync("starting",false,File.Exists(_paths.SecretPath),null,stoppingToken);

            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if(!await EnsureConfiguredAsync(stoppingToken))
                    {
                        await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(2),stoppingToken);
                        continue;
                    }

                    await AcceptAnyReservedAsync(stoppingToken);
                    await DispatchReportsAsync(stoppingToken);

                    var processed=await ProcessOneAsync(stoppingToken);
                    var claimed=false;
                    if(!processed)
                    {
                        claimed=await ClaimAsync(stoppingToken);
                        if(claimed)
                        {
                            await AcceptAnyReservedAsync(stoppingToken);
                            processed=await ProcessOneAsync(stoppingToken);
                        }
                    }

                    _lastPoll=DateTimeOffset.UtcNow;
                    await MaybeHeartbeatAsync(stoppingToken);
                    await MaybeRefreshDestinationsAsync(stoppingToken);
                    await WriteLocalHealthAsync(_consecutiveApiFailures>0?"degraded":"running",true,true,null,stoppingToken);
                    await _wake.WaitOrDelayAsync(
                        TimeSpan.FromMilliseconds(processed||claimed?_options.ActivePollMilliseconds:_options.IdlePollMilliseconds),
                        stoppingToken);
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch(ApiOperationException e)
                {
                    await WriteLocalHealthAsync("degraded",_api is not null,File.Exists(_paths.SecretPath),Safe(e.InnerException?.Message??e.Message),stoppingToken);
                    await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(3),stoppingToken);
                }
                catch(Exception e)
                {
                    LogSafe("loop",e);
                    await WriteLocalHealthAsync("degraded",_api is not null,File.Exists(_paths.SecretPath),Safe(e.Message),stoppingToken);
                    await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(3),stoppingToken);
                }
            }
        }
        finally
        {
            try{await WriteLocalHealthAsync("stopped",_api is not null,File.Exists(_paths.SecretPath),null,CancellationToken.None);}catch{}
            _http?.Dispose();
            _mutex.ReleaseMutex();
        }
    }

    private async Task<bool> EnsureConfiguredAsync(CancellationToken ct)
    {
        var configExists=File.Exists(_paths.ConfigPath);
        var secretExists=File.Exists(_paths.SecretPath);
        if(!configExists||!secretExists)
        {
            _api=null;
            _destinations=[];
            await WriteLocalHealthAsync("waiting_for_configuration",configExists,secretExists,"در انتظار config/token.",ct);
            return false;
        }

        var configStamp=File.GetLastWriteTimeUtc(_paths.ConfigPath);
        var secretStamp=File.GetLastWriteTimeUtc(_paths.SecretPath);
        if(_api is not null&&configStamp==_configStampUtc&&secretStamp==_secretStampUtc)return true;

        HttpClient? candidateHttp=null;
        try
        {
            var options=AgentOptions.Load(_paths.ConfigPath);
            options.Validate();
            var token=SecretStore.Load(_paths.SecretPath);
            if(string.IsNullOrWhiteSpace(token))throw new InvalidDataException("Agent token خالی است.");

            candidateHttp=new HttpClient();
            var candidateApi=new HttpPrintTransport(candidateHttp,options.ServerBaseUrl,token);
            var probe=await RunApiAsync("probe_config",()=>candidateApi.ProbeAsync(ct));
            if(!probe.Success||probe.ProtocolVersion!=4)throw new InvalidOperationException("Print API v4 آماده نیست.");

            _http?.Dispose();
            _http=candidateHttp;
            candidateHttp=null;
            _api=candidateApi;
            _options=options;
            _destinations=probe.Destinations;
            _serverScope=LegacyServerScope; // R11 binds this to a durable server identity in the compatibility phase.
            _configStampUtc=configStamp;
            _secretStampUtc=secretStamp;
            _nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(30);
            _nextHeartbeat=DateTimeOffset.MinValue;

            // A successful authenticated probe is the release condition for reports blocked by the previous token.
            await _reports.ResumeAfterCredentialProbeAsync(_serverScope,ct);
            _fileLog.Info("configured",$"Print API v4 فعال شد؛ {probe.Destinations.Count} مقصد دریافت شد.");
            return true;
        }
        catch(Exception e)
        {
            candidateHttp?.Dispose();
            _api=null;
            _destinations=[];
            if(e is not ApiOperationException)LogSafe("configuration",e);
            await WriteLocalHealthAsync("configuration_error",true,true,Safe(e.InnerException?.Message??e.Message),ct);
            return false;
        }
    }

    private async Task MaybeRefreshDestinationsAsync(CancellationToken ct)
    {
        if(_api is null||DateTimeOffset.UtcNow<_nextDestinationRefresh)return;
        try
        {
            var probe=await RunApiAsync("probe_refresh",()=>_api.ProbeAsync(ct));
            if(probe.Success&&probe.ProtocolVersion==4)_destinations=probe.Destinations;
            _nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(30);
        }
        catch(ApiOperationException)
        {
            _nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(15);
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        foreach(var job in await _store.GetRecoverableAsync(ct))
        {
            var outcome=await _store.GetOutcomeAsync(job.AttemptId,ct);
            if(outcome is not null)
            {
                // Outcome is authoritative. Delivery state is independent and will be replayed by ReportDispatcher.
                continue;
            }

            if(job.State==LocalJobState.WorkerLaunching)
            {
                var result=await TryReadWorkerResultAsync(job,ct);
                if(result is not null)
                {
                    await ApplyWorkerResultAsync(job,result,ct);
                    CleanupWorkerFiles(job);
                    continue;
                }

                if(File.Exists(FencePath(job)))
                {
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,null,false,"service_restart_after_submission_fence","Service پس از Submission Fence بازیابی شد و نتیجه قابل اثبات نیست.","recovery:fence",ct);
                }
                else
                {
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,null,false,"service_restart_worker_state_ambiguous","WorkerLaunching پس از restart بدون شواهد قطعی مرگ child بازیابی شد؛ چاپ مجدد خودکار ممنوع است.","recovery:worker-launch",ct);
                }
                continue;
            }

            switch(job.State)
            {
                case LocalJobState.Submitted when !string.IsNullOrWhiteSpace(job.SpoolerJobId):
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Submitted,job.SpoolerJobId,false,null,null,"recovery:legacy-submitted",ct);
                    break;
                case LocalJobState.ReportPending:
                    // ReportPending is a delivery-era state from 6.2 and does NOT identify a print outcome.
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,job.SpoolerJobId,false,"reportpending_without_outcome","ReportPending بدون Outcome پایدار قابل تفسیر نیست؛ submitted حدس زده نمی‌شود.","recovery:reportpending-no-outcome",ct);
                    break;
                case LocalJobState.SafeFailed:
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Failed,null,true,"recovered_pre_submit_failure",job.LastError,"recovery:safe-failed",ct);
                    break;
                case LocalJobState.Unknown:
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Unknown,job.SpoolerJobId,false,"recovered_unknown",job.LastError,"recovery:unknown",ct);
                    break;
                case LocalJobState.RecoveryHold:
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,job.SpoolerJobId,false,"recovered_hold",job.LastError,"recovery:hold",ct);
                    break;
            }
        }
    }

    private async Task<bool> ClaimAsync(CancellationToken ct)
    {
        if(_api is null||_destinations.Count==0)return false;
        var pending=await LoadPendingClaimAsync(ct);
        if(pending is null)
        {
            var health=SafeQueues();
            var ready=_destinations
                .Where(d=>health.Any(p=>QueueReady(p,d.WindowsQueueName)))
                .Select(d=>d.DestinationKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if(ready.Length==0)return false;

            pending=new ClaimRequestEnvelope(CryptoUtil.NewRequestId(),AgentVersion,4,ready,_options.ClaimBatchSize,DateTimeOffset.UtcNow.ToString("O"));
            await _store.SetMetaAsync(PendingClaimMetaKey,JsonSerializer.Serialize(pending,AgentOptions.JsonOptions()),ct);
        }

        var response=await RunApiAsync("claim",()=>_api.ClaimAsync(pending,ct));
        ValidateClaimResponse(response,pending);
        foreach(var item in response.Jobs)
        {
            await _store.PersistReservedAsync(item,CryptoUtil.NewLocalReceiptId(),_serverScope,ct);
        }
        await _store.DeleteMetaAsync(PendingClaimMetaKey,ct);
        return response.Jobs.Count>0;
    }

    private static void ValidateClaimResponse(ClaimResponse response,ClaimRequestEnvelope request)
    {
        if(!response.Success)throw new InvalidDataException("Claim response success=false است.");
        if(!string.Equals(response.RequestId,request.RequestId,StringComparison.Ordinal))throw new InvalidDataException("Claim response request_id mismatch.");
    }

    private async Task<ClaimRequestEnvelope?> LoadPendingClaimAsync(CancellationToken ct)
    {
        var raw=await _store.GetMetaAsync(PendingClaimMetaKey,ct);
        if(string.IsNullOrWhiteSpace(raw))return null;
        try
        {
            var pending=JsonSerializer.Deserialize<ClaimRequestEnvelope>(raw,AgentOptions.JsonOptions());
            if(pending is null||string.IsNullOrWhiteSpace(pending.RequestId)||pending.ProtocolVersion!=4||pending.ReadyDestinationKeys.Length==0||pending.Limit is <1 or >5)
                throw new InvalidDataException("Pending claim metadata نامعتبر است.");
            return pending;
        }
        catch(Exception e)
        {
            LogSafe("claim_replay_metadata",e);
            await _store.DeleteMetaAsync(PendingClaimMetaKey,ct);
            return null;
        }
    }

    private async Task AcceptAnyReservedAsync(CancellationToken ct)
    {
        if(_api is null)return;
        foreach(var local in (await _store.GetRecoverableAsync(ct)).Where(x=>x.State==LocalJobState.Reserved).OrderBy(x=>x.ServerJobId).ThenBy(x=>x.AttemptNo))
        {
            var reqKey=$"accept_request:{local.AttemptId}";
            var requestId=await StableRequestIdAsync(reqKey,ct);
            if(local.LeaseExpiresAt<=DateTimeOffset.UtcNow)
            {
                try
                {
                    var status=await RunApiAsync("attempt_status",()=>_api.AttemptStatusAsync(local,ct));
                    if(!ValidateAttemptStatusIdentity(status,local))continue;
                    if(status.RequiresHumanResolution)continue;
                    if(status.AttemptState is "claimed" or "started")
                    {
                        await _store.SetStateAsync(local.AttemptId,LocalJobState.Claimed,ct:ct);
                        await _store.DeleteMetaAsync(reqKey,ct);
                        continue;
                    }
                    if(status.Terminal&&status.AttemptState is "expired" or "cancelled" or "failed")
                    {
                        await _store.SetStateAsync(local.AttemptId,LocalJobState.Resolved,error:$"Server state: {status.AttemptState}",ct:ct);
                        await _store.DeleteMetaAsync(reqKey,ct);
                    }
                    continue;
                }
                catch(ApiOperationException){continue;}
            }

            var destination=new DestinationConfig(local.DestinationKey,local.DestinationKey,local.QueueName,local.PaperWidthMm,local.PrintableWidthMm,local.Copies,local.LayoutMode);
            var item=new ClaimItem(
                new((int)local.ServerJobId,"","",false,null,null,"",4,local.ContentSha256,local.PayloadJson),
                new(local.AttemptId,local.AttemptNo,SecretStore.UnprotectText(local.ProtectedLeaseToken),local.LeaseExpiresAt.ToString("O")),
                destination);
            try
            {
                var result=await RunApiAsync("accept",()=>_api.AcceptAsync(item,local.LocalReceiptId,requestId,ct));
                if(ValidateMutationResponse(result,local,"accept"))
                {
                    await _store.SetStateAsync(local.AttemptId,LocalJobState.Claimed,ct:ct);
                    await _store.DeleteMetaAsync(reqKey,ct);
                }
            }
            catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException {Code:"lease_expired"})
            {
                // Reconcile on the next pass with the same local receipt and request identity.
            }
        }
    }

    private static bool ValidateAttemptStatusIdentity(AttemptStatusResult result,LocalJob local)
    {
        if(!result.Success)return false;
        if(result.AttemptId!=local.AttemptId||result.JobId!=local.ServerJobId)return false;
        if(!result.ReceiptMatches)return false;
        if(result.RequiresHumanResolution)return false;
        if(string.IsNullOrWhiteSpace(result.AttemptState)||string.IsNullOrWhiteSpace(result.NextAction))return false;
        return true;
    }

    private static bool ValidateMutationResponse(ApiResult result,LocalJob local,string action)
    {
        if(!result.Success||result.RequiresHumanResolution)return false;
        if(result.AttemptId is { } attempt&&attempt!=local.AttemptId)return false;
        if(result.JobId is { } job&&job!=local.ServerJobId)return false;
        if(!string.IsNullOrWhiteSpace(result.LocalReceiptId)&&!string.Equals(result.LocalReceiptId,local.LocalReceiptId,StringComparison.Ordinal))return false;
        if(action=="accept"&&!string.IsNullOrWhiteSpace(result.Status)&&result.Status is not ("claimed" or "started"))return false;
        if(action=="start"&&!string.IsNullOrWhiteSpace(result.Status)&&result.Status!="started")return false;
        return true;
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        if(_api is null)return false;
        var queues=SafeQueues();
        var open=await _store.GetRecoverableAsync(ct);
        var job=open
            .Where(x=>x.State==LocalJobState.Claimed)
            .OrderBy(x=>x.ServerJobId)
            .ThenBy(x=>x.AttemptNo)
            .FirstOrDefault(candidate=>
                queues.Any(q=>QueueReady(q,candidate.QueueName))&&
                !open.Any(older=>string.Equals(older.DestinationKey,candidate.DestinationKey,StringComparison.OrdinalIgnoreCase)&&older.ServerJobId<candidate.ServerJobId));
        if(job is null)return false;

        var startKey=$"start_request:{job.AttemptId}";
        var startRequestId=await StableRequestIdAsync(startKey,ct);
        try
        {
            var started=await RunApiAsync("start",()=>_api.StartAsync(job,startRequestId,ct));
            if(!ValidateMutationResponse(started,job,"start"))
            {
                await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,null,false,"invalid_start_response","Start response هویت/مجوز معتبر نداشت.","server:start-validation",ct);
                return true;
            }
            await _store.DeleteMetaAsync(startKey,ct);
        }
        catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException {Terminal:true} e)
        {
            await _store.SetStateAsync(job.AttemptId,LocalJobState.Resolved,error:$"Server state: {e.CurrentState??"terminal"}",ct:ct);
            await _store.DeleteMetaAsync(startKey,ct);
            return true;
        }

        CleanupTransientBeforeLaunch(job);
        var input=new WorkerInput(job.ServerJobId,job.AttemptId,job.LocalReceiptId,job.QueueName,job.PayloadJson,job.ContentSha256,job.PaperWidthMm,job.PrintableWidthMm,job.Copies,ResultPath(job),FencePath(job),StartSignalPath(job));
        await DurableFile.WriteJsonAtomicAsync(InputPath(job),input,ct);
        await _store.SetStateAsync(job.AttemptId,LocalJobState.WorkerLaunching,markWorkerLaunching:true,ct:ct);

        Process? process=null;
        WorkerProcessGuard? guard=null;
        try
        {
            var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));
            var psi=new ProcessStartInfo(worker,$"\"{InputPath(job)}\"")
            {
                UseShellExecute=false,
                CreateNoWindow=true,
                WorkingDirectory=Path.GetDirectoryName(worker)!,
                RedirectStandardError=true
            };
            process=Process.Start(psi)??throw new InvalidOperationException("PrintWorker اجرا نشد.");
            try
            {
                guard=WorkerProcessGuard.Attach(process);
                await DurableFile.TouchAtomicAsync(StartSignalPath(job),$"start:{job.AttemptId}",ct);
            }
            catch(Exception launchFault)
            {
                TryKill(process);
                await WaitForExitProofAsync(process);
                await FinalizeStoppedWorkerAsync(job,process,launchFault,"worker_launch_failure",ct);
                return true;
            }

            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.WorkerTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {
                TryKill(process);
                await WaitForExitProofAsync(process);
                await FinalizeStoppedWorkerAsync(job,process,null,"worker_timeout",CancellationToken.None);
                return true;
            }

            var result=await TryReadWorkerResultAsync(job,ct);
            if(result is not null)await ApplyWorkerResultAsync(job,result,ct);
            else await FinalizeStoppedWorkerAsync(job,process,null,"worker_exit_without_result",ct);
            CleanupWorkerFiles(job);
            return true;
        }
        catch(Exception e) when(process is null)
        {
            await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Failed,null,true,"worker_process_start_failed",Safe(e.Message),"service:process-start",ct);
            CleanupWorkerFiles(job);
            return true;
        }
        finally
        {
            guard?.Dispose();
            process?.Dispose();
        }
    }

    private async Task FinalizeStoppedWorkerAsync(LocalJob job,Process? process,Exception? failure,string code,CancellationToken ct)
    {
        var durable=await TryReadWorkerResultAsync(job,ct);
        if(durable is not null)
        {
            await ApplyWorkerResultAsync(job,durable,ct);
            CleanupWorkerFiles(job);
            return;
        }

        var error=failure is null?string.Empty:Safe(failure.Message);
        if(process is not null&&process.HasExited&&process.StartInfo.RedirectStandardError)
        {
            try
            {
                var stderr=Safe(await process.StandardError.ReadToEndAsync(ct));
                if(stderr.Length>0)error=stderr;
            }
            catch{}
        }

        if(File.Exists(FencePath(job)))
        {
            await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,null,false,code+"_after_fence",error.Length>0?error:"Worker پس از Submission Fence بدون نتیجه قطعی متوقف شد؛ Retry خودکار ممنوع است.","supervisor:fence",ct);
        }
        else
        {
            await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Failed,null,true,code+"_before_fence",error.Length>0?error:"توقف Worker پیش از Submission Fence اثبات شد؛ Retry ایمن مجاز است.","supervisor:pre-fence",ct);
        }
        CleanupWorkerFiles(job);
    }

    // R05 replaces this with a bounded/testable WorkerSupervisor. Until then it remains the 6.2 behavior.
    private static async Task WaitForExitProofAsync(Process process)
    {
        try{if(!process.HasExited)await process.WaitForExitAsync(CancellationToken.None);}catch{}
    }

    private async Task<WorkerResult?> TryReadWorkerResultAsync(LocalJob job,CancellationToken ct)
    {
        var path=ResultPath(job);
        if(!File.Exists(path))return null;
        try
        {
            var result=JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(path,ct),AgentOptions.JsonOptions());
            if(result is null||
               result.ServerJobId!=job.ServerJobId||
               result.AttemptId!=job.AttemptId||
               !string.Equals(result.LocalReceiptId,job.LocalReceiptId,StringComparison.Ordinal)||
               !string.Equals(result.ContentSha256,job.ContentSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Durable Worker result با Attempt محلی تطابق ندارد.");
            return result;
        }
        catch(Exception e)
        {
            LogSafe("worker_result_validation",e);
            return null;
        }
    }

    private async Task ApplyWorkerResultAsync(LocalJob job,WorkerResult result,CancellationToken ct)
    {
        switch(result.Status)
        {
            case "submitted":
                if(string.IsNullOrWhiteSpace(result.SpoolerJobId))
                {
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,null,false,"submitted_without_spooler_id","Worker وضعیت submitted بدون Spooler Job ID ثبت کرده است.","worker:result-validation",ct);
                    return;
                }
                _lastSubmission=DateTimeOffset.UtcNow;
                await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Submitted,result.SpoolerJobId,false,null,null,"worker:durable-result",ct);
                return;
            case "failed":
                await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Failed,null,result.Retryable,result.ErrorCode,result.ErrorMessage,"worker:durable-result",ct);
                return;
            case "unknown":
                await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Unknown,result.SpoolerJobId,false,result.ErrorCode,result.ErrorMessage,"worker:durable-result",ct);
                return;
            default:
                await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,result.SpoolerJobId,false,result.ErrorCode??"worker_recovery_hold",result.ErrorMessage,"worker:durable-result",ct);
                return;
        }
    }

    private async Task PersistOutcomeAndReportAsync(
        LocalJob job,
        PrintOutcomeStatus status,
        string? spooler,
        bool retryable,
        string? code,
        string? message,
        string provenance,
        CancellationToken ct)
    {
        var outcome=new AttemptOutcomeDraft(status,spooler,retryable,code,message,provenance);
        var report=new ReportRequestEnvelope(
            CryptoUtil.NewRequestId(),
            AgentVersion,
            4,
            job.AttemptId,
            job.LocalReceiptId,
            LocalQueueStore.ToWireStatus(status),
            spooler,
            retryable,
            code,
            message);
        await _store.CommitOutcomeAndReportAsync(job,outcome,report,ct);
    }

    private async Task DispatchReportsAsync(CancellationToken ct)
    {
        if(_api is null)return;
        var summary=await _reports.DispatchBatchAsync(_api,_serverScope,20,ct);
        if(summary.Attempted==0)return;
        if(summary.LastErrorCode is not null)
        {
            _lastApiErrorCode=summary.LastErrorCode;
            _consecutiveApiFailures++;
        }
        else if(summary.Delivered>0)
        {
            _lastSuccessfulAction="report";
            _lastApiSuccess=DateTimeOffset.UtcNow;
            _lastApiErrorCode=null;
            _consecutiveApiFailures=0;
        }
    }

    private async Task MaybeHeartbeatAsync(CancellationToken ct)
    {
        if(_api is null||DateTimeOffset.UtcNow<_nextHeartbeat)return;
        var queues=SafeQueues();
        _printerDiscoveryAt=DateTimeOffset.UtcNow;
        var pairing=_options.LocalBridgeEnabled?LocalBridgeService.GetOrCreatePairingId(_paths):null;
        var origin=ResolveBridgeOrigin();
        var reportCounts=await _store.GetReportStateCountsAsync(ct);
        var payload=new HeartbeatPayload(
            CryptoUtil.NewRequestId(),Environment.MachineName,AgentVersion,Environment.OSVersion.VersionString,
            (long)(DateTimeOffset.UtcNow-_started).TotalSeconds,
            _lastPoll?.ToString("O"),
            await _store.CountOpenAsync(ct),
            await _store.CountAmbiguousAsync(ct),
            _lastSubmission?.ToString("O"),
            "ok",
            DiskFreeMb(),
            File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"))),
            true,true,
            queues.ToList(),
            _lastSuccessfulAction,
            _lastApiSuccess?.ToString("O"),
            _lastApiErrorCode,
            _consecutiveApiFailures,
            _lastApiLatencyMs,
            _printerDiscoveryAt?.ToString("O"),
            _options.LocalBridgeEnabled?1:0,
            _options.LocalBridgeEnabled?_options.LocalBridgePort:0,
            pairing,
            origin,
            reportCounts.Pending+reportCounts.Backoff,
            reportCounts.AuthBlocked,
            reportCounts.ReconciliationRequired);
        try
        {
            await RunApiAsync("heartbeat",()=>_api.HeartbeatAsync(payload,ct));
            _nextHeartbeat=DateTimeOffset.UtcNow.AddSeconds(_options.HeartbeatSeconds);
        }
        catch(ApiOperationException)
        {
            _nextHeartbeat=DateTimeOffset.UtcNow.AddSeconds(Math.Max(10,_options.HeartbeatSeconds));
        }
    }

    private string? ResolveBridgeOrigin()
    {
        var raw=string.IsNullOrWhiteSpace(_options.LocalBridgeAllowedOrigin)?_options.ServerBaseUrl:_options.LocalBridgeAllowedOrigin;
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri))return null;
        return new UriBuilder(uri.Scheme,uri.Host,uri.IsDefaultPort?-1:uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task<T> RunApiAsync<T>(string action,Func<Task<T>> call)
    {
        var stopwatch=Stopwatch.StartNew();
        try
        {
            var result=await call();
            stopwatch.Stop();
            _lastSuccessfulAction=action;
            _lastApiSuccess=DateTimeOffset.UtcNow;
            _lastApiErrorCode=null;
            _lastApiLatencyMs=stopwatch.ElapsedMilliseconds;
            _consecutiveApiFailures=0;
            return result;
        }
        catch(Exception e)
        {
            stopwatch.Stop();
            _lastApiLatencyMs=stopwatch.ElapsedMilliseconds;
            _lastApiErrorCode=e is PrintApiException api&&!string.IsNullOrWhiteSpace(api.Code)?api.Code:e.GetType().Name;
            _consecutiveApiFailures++;
            LogSafe(action,e);
            throw new ApiOperationException(action,e);
        }
    }

    private async Task<string> StableRequestIdAsync(string key,CancellationToken ct)
    {
        var existing=await _store.GetMetaAsync(key,ct);
        if(!string.IsNullOrWhiteSpace(existing))return existing;
        var created=CryptoUtil.NewRequestId();
        await _store.SetMetaAsync(key,created,ct);
        return created;
    }

    private async Task WriteLocalHealthAsync(string state,bool configOk,bool secretOk,string? error,CancellationToken ct)
    {
        try
        {
            var serviceAccount=OperatingSystem.IsWindows()&&WindowsIdentity.GetCurrent().IsSystem;
            var counts=await _store.GetReportStateCountsAsync(ct);
            var oldest=await _store.GetOldestUndeliveredReportAgeSecondsAsync(ct);
            var snapshot=new LocalHealthSnapshot(
                AgentVersion,
                Environment.MachineName,
                state,
                configOk,
                secretOk,
                serviceAccount,
                error,
                DateTimeOffset.UtcNow.ToString("O"),
                await _store.CountOpenAsync(ct),
                await _store.CountAmbiguousAsync(ct),
                SafeQueues().ToList(),
                _lastSuccessfulAction,
                _lastApiSuccess?.ToString("O"),
                _lastApiErrorCode,
                _consecutiveApiFailures,
                _lastApiLatencyMs,
                counts.Pending,
                counts.Backoff,
                counts.AuthBlocked,
                counts.ReconciliationRequired,
                oldest);
            await DurableFile.WriteJsonAtomicAsync(_paths.HealthPath,snapshot,ct);
        }
        catch(Exception e)
        {
            _log.LogWarning("health.json: {Type}: {Message}",e.GetType().Name,Safe(e.Message));
        }
    }

    private IReadOnlyList<PrinterQueueHealth> SafeQueues()
    {
        try{return _printers.GetQueues();}
        catch(Exception e){LogSafe("printer_health",e);return[];}
    }

    private static bool QueueReady(PrinterQueueHealth printer,string queue)
        => string.Equals(printer.Name,queue,StringComparison.OrdinalIgnoreCase)&&!printer.Offline&&!printer.Paused&&!printer.PaperOut&&!printer.Error;

    private long DiskFreeMb()
    {
        try
        {
            var root=Path.GetPathRoot(_paths.ProgramDataRoot);
            return root is null?0:new DriveInfo(root).AvailableFreeSpace/1024/1024;
        }
        catch{return 0;}
    }

    private string InputPath(LocalJob job)=>Path.Combine(_paths.WorkPath,$"input-{job.ServerJobId}-{job.AttemptId}.json");
    private string ResultPath(LocalJob job)=>Path.Combine(_paths.WorkPath,$"result-{job.ServerJobId}-{job.AttemptId}.json");
    private string FencePath(LocalJob job)=>Path.Combine(_paths.WorkPath,$"fence-{job.ServerJobId}-{job.AttemptId}.dat");
    private string StartSignalPath(LocalJob job)=>Path.Combine(_paths.WorkPath,$"start-{job.ServerJobId}-{job.AttemptId}.dat");

    private void CleanupTransientBeforeLaunch(LocalJob job)
    {
        TryDelete(InputPath(job));
        TryDelete(ResultPath(job));
        TryDelete(FencePath(job));
        TryDelete(StartSignalPath(job));
    }

    private void CleanupWorkerFiles(LocalJob job)
    {
        TryDelete(InputPath(job));
        TryDelete(ResultPath(job));
        TryDelete(FencePath(job));
        TryDelete(StartSignalPath(job));
    }

    private void LogSafe(string area,Exception e)
    {
        var message=Safe(e.Message);
        _log.LogError("{Area}: {Type}: {Message}",area,e.GetType().Name,message);
        try{_fileLog.Error(area,new InvalidOperationException(message));}
        catch(Exception logError){_log.LogError("FileLog: {Type}: {Message}",logError.GetType().Name,Safe(logError.Message));}
    }

    private static string Safe(string value)=>SafeLogText.Sanitize(value,400);
    private static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}
    private static void TryKill(Process process){try{if(!process.HasExited)process.Kill(true);}catch{}}

    private sealed class ApiOperationException : Exception
    {
        public string Action { get; }
        public ApiOperationException(string action,Exception inner):base($"Print API {action} failed.",inner)=>Action=action;
    }
}
