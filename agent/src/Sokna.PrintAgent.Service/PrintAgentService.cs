using System.Diagnostics;
using System.Net;
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
    private readonly AgentPaths _paths;private readonly LocalQueueStore _store;private readonly IPrinterHealthProvider _printers;private readonly ILogger<PrintAgentService> _log;private readonly AgentLog _fileLog;private readonly PrintWakeSignal _wake;
    private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;private readonly Mutex _mutex=new(false,@"Global\SoknaPrintAgentV6Service");
    private AgentOptions _options=new();private IPrintTransport? _api;private HttpClient? _http;private DateTime _configStampUtc=DateTime.MinValue;private DateTime _secretStampUtc=DateTime.MinValue;
    private DateTimeOffset? _lastPoll;private DateTimeOffset? _lastSubmission;private DateTimeOffset? _lastApiSuccess;private DateTimeOffset? _printerDiscoveryAt;private string? _lastSuccessfulAction;private string? _lastApiErrorCode;private int _consecutiveApiFailures;private long? _lastApiLatencyMs;private DateTimeOffset _nextHeartbeat=DateTimeOffset.MinValue;private DateTimeOffset _nextDestinationRefresh=DateTimeOffset.MinValue;private IReadOnlyList<DestinationConfig> _destinations=[];
    public PrintAgentService(AgentPaths paths,LocalQueueStore store,IPrinterHealthProvider printers,ILogger<PrintAgentService> log,AgentLog fileLog,PrintWakeSignal wake){_paths=paths;_store=store;_printers=printers;_log=log;_fileLog=fileLog;_wake=wake;}

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!_mutex.WaitOne(TimeSpan.Zero))throw new InvalidOperationException("نمونه دیگری از Sokna Print Agent فعال است.");
        try
        {
            await _store.InitializeAsync(stoppingToken);await RecoverAsync(stoppingToken);await WriteLocalHealthAsync("starting",false,File.Exists(_paths.SecretPath),null,stoppingToken);
            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if(!await EnsureConfiguredAsync(stoppingToken)){await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(2),stoppingToken);continue;}
                    await AcceptAnyReservedAsync(stoppingToken);await FlushReportsAsync(stoppingToken);
                    var processed=await ProcessOneAsync(stoppingToken);var claimed=false;
                    if(!processed){claimed=await ClaimAsync(stoppingToken);if(claimed){await AcceptAnyReservedAsync(stoppingToken);processed=await ProcessOneAsync(stoppingToken);}}
                    _lastPoll=DateTimeOffset.UtcNow;await MaybeHeartbeatAsync(stoppingToken);await MaybeRefreshDestinationsAsync(stoppingToken);
                    await WriteLocalHealthAsync(_consecutiveApiFailures>0?"degraded":"running",true,true,null,stoppingToken);
                    await _wake.WaitOrDelayAsync(TimeSpan.FromMilliseconds(processed||claimed?_options.ActivePollMilliseconds:_options.IdlePollMilliseconds),stoppingToken);
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
                catch(ApiOperationException e){await WriteLocalHealthAsync("degraded",_api is not null,File.Exists(_paths.SecretPath),Safe(e.InnerException?.Message??e.Message),stoppingToken);await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(3),stoppingToken);}
                catch(Exception e){LogSafe("loop",e);await WriteLocalHealthAsync("degraded",_api is not null,File.Exists(_paths.SecretPath),Safe(e.Message),stoppingToken);await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(3),stoppingToken);}
            }
        }
        finally{try{await WriteLocalHealthAsync("stopped",_api is not null,File.Exists(_paths.SecretPath),null,CancellationToken.None);}catch{} _http?.Dispose();_mutex.ReleaseMutex();}
    }

    private async Task<bool> EnsureConfiguredAsync(CancellationToken ct)
    {
        var configExists=File.Exists(_paths.ConfigPath);var secretExists=File.Exists(_paths.SecretPath);
        if(!configExists||!secretExists){_api=null;_destinations=[];await WriteLocalHealthAsync("waiting_for_configuration",configExists,secretExists,"در انتظار config/token.",ct);return false;}
        var configStamp=File.GetLastWriteTimeUtc(_paths.ConfigPath);var secretStamp=File.GetLastWriteTimeUtc(_paths.SecretPath);if(_api is not null&&configStamp==_configStampUtc&&secretStamp==_secretStampUtc)return true;
        HttpClient? candidateHttp=null;
        try
        {
            var options=AgentOptions.Load(_paths.ConfigPath);options.Validate();var token=SecretStore.Load(_paths.SecretPath);if(string.IsNullOrWhiteSpace(token))throw new InvalidDataException("Agent token خالی است.");candidateHttp=new HttpClient();var candidateApi=new HttpPrintTransport(candidateHttp,options.ServerBaseUrl,token);var probe=await RunApiAsync("probe_config",()=>candidateApi.ProbeAsync(ct));if(!probe.Success||probe.ProtocolVersion!=4)throw new InvalidOperationException("Print API v4 آماده نیست.");
            _http?.Dispose();_http=candidateHttp;candidateHttp=null;_api=candidateApi;_options=options;_destinations=probe.Destinations;_configStampUtc=configStamp;_secretStampUtc=secretStamp;_nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(30);_nextHeartbeat=DateTimeOffset.MinValue;_fileLog.Info("configured",$"Print API v4 فعال شد؛ {probe.Destinations.Count} مقصد دریافت شد.");return true;
        }
        catch(Exception e){candidateHttp?.Dispose();_api=null;_destinations=[];if(e is not ApiOperationException)LogSafe("configuration",e);await WriteLocalHealthAsync("configuration_error",true,true,Safe(e.InnerException?.Message??e.Message),ct);return false;}
    }
    private async Task MaybeRefreshDestinationsAsync(CancellationToken ct){if(_api is null||DateTimeOffset.UtcNow<_nextDestinationRefresh)return;try{var probe=await RunApiAsync("probe_refresh",()=>_api.ProbeAsync(ct));if(probe.Success&&probe.ProtocolVersion==4)_destinations=probe.Destinations;_nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(30);}catch(ApiOperationException){_nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(15);}}

    private async Task RecoverAsync(CancellationToken ct)
    {
        foreach(var job in await _store.GetRecoverableAsync(ct))
        {
            if(job.State==LocalJobState.WorkerLaunching)
            {
                var result=await TryReadWorkerResultAsync(job,ct);if(result is not null){await ApplyWorkerResultAsync(job,result,ct);CleanupWorkerFiles(job);continue;}
                // On service restart we cannot prove an untracked child died if launch/guard failed. Absence of a fence is not proof of non-submission.
                if(File.Exists(FencePath(job)))await QueueReportAsync(job,"recovery_hold",null,false,"service_restart_after_submission_fence","Service پس از Submission Fence بازیابی شد و نتیجه قابل اثبات نیست.",ct);
                else await QueueReportAsync(job,"recovery_hold",null,false,"service_restart_worker_state_ambiguous","WorkerLaunching پس از restart بدون شواهد قطعی مرگ child بازیابی شد؛ چاپ مجدد خودکار ممنوع است.",ct);
            }
            else if(job.State is LocalJobState.Submitted or LocalJobState.ReportPending)await QueueReportAsync(job,"submitted",job.SpoolerJobId,false,null,null,ct);
            else if(job.State==LocalJobState.Unknown)await QueueReportAsync(job,"unknown",job.SpoolerJobId,false,"recovered_unknown",job.LastError,ct);
            else if(job.State==LocalJobState.RecoveryHold)await QueueReportAsync(job,"recovery_hold",job.SpoolerJobId,false,"recovered_hold",job.LastError,ct);
            else if(job.State==LocalJobState.SafeFailed)await QueueReportAsync(job,"failed",null,true,"recovered_pre_submit_failure",job.LastError,ct);
        }
    }

    private async Task<bool> ClaimAsync(CancellationToken ct)
    {
        if(_api is null||_destinations.Count==0)return false;var pending=await LoadPendingClaimAsync(ct);
        if(pending is null)
        {
            var health=SafeQueues();var ready=_destinations.Where(d=>health.Any(p=>QueueReady(p,d.WindowsQueueName))).Select(d=>d.DestinationKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(ready.Length==0)return false;
            pending=new ClaimRequestEnvelope(CryptoUtil.NewRequestId(),AgentVersion,4,ready,_options.ClaimBatchSize,DateTimeOffset.UtcNow.ToString("O"));await _store.SetMetaAsync(PendingClaimMetaKey,JsonSerializer.Serialize(pending,AgentOptions.JsonOptions()),ct);
        }
        var response=await RunApiAsync("claim",()=>_api.ClaimAsync(pending,ct));foreach(var item in response.Jobs)await _store.PersistReservedAsync(item,CryptoUtil.NewLocalReceiptId(),ct);await _store.DeleteMetaAsync(PendingClaimMetaKey,ct);return response.Jobs.Count>0;
    }
    private async Task<ClaimRequestEnvelope?> LoadPendingClaimAsync(CancellationToken ct)
    {
        var raw=await _store.GetMetaAsync(PendingClaimMetaKey,ct);if(string.IsNullOrWhiteSpace(raw))return null;
        try{var pending=JsonSerializer.Deserialize<ClaimRequestEnvelope>(raw,AgentOptions.JsonOptions());if(pending is null||string.IsNullOrWhiteSpace(pending.RequestId)||pending.ProtocolVersion!=4||pending.ReadyDestinationKeys.Length==0||pending.Limit is <1 or >5)throw new InvalidDataException("Pending claim metadata نامعتبر است.");return pending;}
        catch(Exception e){LogSafe("claim_replay_metadata",e);await _store.DeleteMetaAsync(PendingClaimMetaKey,ct);return null;}
    }

    private async Task AcceptAnyReservedAsync(CancellationToken ct)
    {
        if(_api is null)return;
        foreach(var local in (await _store.GetRecoverableAsync(ct)).Where(x=>x.State==LocalJobState.Reserved).OrderBy(x=>x.ServerJobId).ThenBy(x=>x.AttemptNo))
        {
            var reqKey=$"accept_request:{local.AttemptId}";var requestId=await StableRequestIdAsync(reqKey,ct);
            if(local.LeaseExpiresAt<=DateTimeOffset.UtcNow)
            {
                try
                {
                    var status=await RunApiAsync("attempt_status",()=>_api.AttemptStatusAsync(local,ct));
                    if(status.AttemptState is "claimed" or "started"){await _store.SetStateAsync(local.AttemptId,LocalJobState.Claimed,ct:ct);await _store.DeleteMetaAsync(reqKey,ct);continue;}
                    if(status.Terminal&&status.AttemptState is "expired" or "cancelled" or "failed"){await _store.SetStateAsync(local.AttemptId,LocalJobState.Resolved,error:$"Server state: {status.AttemptState}",ct:ct);await _store.DeleteMetaAsync(reqKey,ct);continue;}
                    // Server still owns the ambiguity; keep the local Reserved record and reconcile again later.
                    continue;
                }
                catch(ApiOperationException){continue;}
            }
            var destination=new DestinationConfig(local.DestinationKey,local.DestinationKey,local.QueueName,local.PaperWidthMm,local.PrintableWidthMm,local.Copies,local.LayoutMode);
            var item=new ClaimItem(new((int)local.ServerJobId,"","",false,null,null,"",4,local.ContentSha256,local.PayloadJson),new(local.AttemptId,local.AttemptNo,SecretStore.UnprotectText(local.ProtectedLeaseToken),local.LeaseExpiresAt.ToString("O")),destination);
            try{var result=await RunApiAsync("accept",()=>_api.AcceptAsync(item,local.LocalReceiptId,requestId,ct));if(result.Success){await _store.SetStateAsync(local.AttemptId,LocalJobState.Claimed,ct:ct);await _store.DeleteMetaAsync(reqKey,ct);}}
            catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException {Code:"lease_expired"}){/* reconcile on next pass with the same receipt/request identity */}
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        if(_api is null)return false;var queues=SafeQueues();var open=await _store.GetRecoverableAsync(ct);var job=open.Where(x=>x.State==LocalJobState.Claimed).OrderBy(x=>x.ServerJobId).ThenBy(x=>x.AttemptNo).FirstOrDefault(candidate=>queues.Any(q=>QueueReady(q,candidate.QueueName))&&!open.Any(older=>string.Equals(older.DestinationKey,candidate.DestinationKey,StringComparison.OrdinalIgnoreCase)&&older.ServerJobId<candidate.ServerJobId));if(job is null)return false;
        var startKey=$"start_request:{job.AttemptId}";var startRequestId=await StableRequestIdAsync(startKey,ct);
        try{var started=await RunApiAsync("start",()=>_api.StartAsync(job,startRequestId,ct));if(!started.Success)return false;await _store.DeleteMetaAsync(startKey,ct);}
        catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException {Terminal:true} e){await _store.SetStateAsync(job.AttemptId,LocalJobState.Resolved,error:$"Server state: {e.CurrentState??"terminal"}",ct:ct);await _store.DeleteMetaAsync(startKey,ct);return true;}

        CleanupTransientBeforeLaunch(job);var input=new WorkerInput(job.ServerJobId,job.AttemptId,job.LocalReceiptId,job.QueueName,job.PayloadJson,job.ContentSha256,job.PaperWidthMm,job.PrintableWidthMm,job.Copies,ResultPath(job),FencePath(job),StartSignalPath(job));await DurableFile.WriteJsonAtomicAsync(InputPath(job),input,ct);await _store.SetStateAsync(job.AttemptId,LocalJobState.WorkerLaunching,markWorkerLaunching:true,ct:ct);
        Process? process=null;WorkerProcessGuard? guard=null;
        try
        {
            var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));var psi=new ProcessStartInfo(worker,$"\"{InputPath(job)}\""){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(worker)!,RedirectStandardError=true};process=Process.Start(psi)??throw new InvalidOperationException("PrintWorker اجرا نشد.");
            try{guard=WorkerProcessGuard.Attach(process);await DurableFile.TouchAtomicAsync(StartSignalPath(job),$"start:{job.AttemptId}",ct);}catch(Exception launchFault){TryKill(process);await WaitForExitProofAsync(process);await FinalizeStoppedWorkerAsync(job,process,launchFault,"worker_launch_failure",ct);return true;}
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(_options.WorkerTimeoutSeconds));
            try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException) when(!ct.IsCancellationRequested){TryKill(process);await WaitForExitProofAsync(process);await FinalizeStoppedWorkerAsync(job,process,null,"worker_timeout",CancellationToken.None);return true;}
            var result=await TryReadWorkerResultAsync(job,ct);if(result is not null)await ApplyWorkerResultAsync(job,result,ct);else await FinalizeStoppedWorkerAsync(job,process,null,"worker_exit_without_result",ct);CleanupWorkerFiles(job);return true;
        }
        catch(Exception e) when(process is null){await QueueReportAsync(job,"failed",null,true,"worker_process_start_failed",Safe(e.Message),ct);CleanupWorkerFiles(job);return true;}
        finally{guard?.Dispose();process?.Dispose();}
    }
    private async Task FinalizeStoppedWorkerAsync(LocalJob job,Process? process,Exception? failure,string code,CancellationToken ct)
    {
        var durable=await TryReadWorkerResultAsync(job,ct);if(durable is not null){await ApplyWorkerResultAsync(job,durable,ct);CleanupWorkerFiles(job);return;}
        var err=failure is null?"":Safe(failure.Message);if(process is not null&&process.HasExited&&process.StartInfo.RedirectStandardError){try{var stderr=Safe(await process.StandardError.ReadToEndAsync(ct));if(stderr.Length>0)err=stderr;}catch{}}
        if(File.Exists(FencePath(job)))await QueueReportAsync(job,"recovery_hold",null,false,code+"_after_fence",err.Length>0?err:"Worker پس از Submission Fence بدون نتیجه قطعی متوقف شد؛ Retry خودکار ممنوع است.",ct);else await QueueReportAsync(job,"failed",null,true,code+"_before_fence",err.Length>0?err:"توقف Worker پیش از Submission Fence اثبات شد؛ Retry ایمن مجاز است.",ct);CleanupWorkerFiles(job);
    }
    private static async Task WaitForExitProofAsync(Process process){try{if(!process.HasExited)await process.WaitForExitAsync(CancellationToken.None);}catch{} }

    private async Task<WorkerResult?> TryReadWorkerResultAsync(LocalJob job,CancellationToken ct)
    {
        var path=ResultPath(job);if(!File.Exists(path))return null;try{var result=JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(path,ct),AgentOptions.JsonOptions());if(result is null||result.ServerJobId!=job.ServerJobId||result.AttemptId!=job.AttemptId||!string.Equals(result.LocalReceiptId,job.LocalReceiptId,StringComparison.Ordinal)||!string.Equals(result.ContentSha256,job.ContentSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Durable Worker result با Attempt محلی تطابق ندارد.");return result;}catch(Exception e){LogSafe("worker_result_validation",e);return null;}
    }
    private async Task ApplyWorkerResultAsync(LocalJob job,WorkerResult result,CancellationToken ct)
    {
        if(result.Status=="submitted"){if(string.IsNullOrWhiteSpace(result.SpoolerJobId)){await QueueReportAsync(job,"recovery_hold",null,false,"submitted_without_spooler_id","Worker وضعیت submitted بدون Spooler Job ID ثبت کرده است.",ct);return;}_lastSubmission=DateTimeOffset.UtcNow;await _store.SetStateAsync(job.AttemptId,LocalJobState.Submitted,result.SpoolerJobId,ct:ct);await QueueReportAsync(job,"submitted",result.SpoolerJobId,false,null,null,ct);}
        else if(result.Status=="failed"){await _store.SetStateAsync(job.AttemptId,LocalJobState.SafeFailed,error:result.ErrorMessage,ct:ct);await QueueReportAsync(job,"failed",null,result.Retryable,result.ErrorCode,result.ErrorMessage,ct);}
        else if(result.Status=="unknown"){await _store.SetStateAsync(job.AttemptId,LocalJobState.Unknown,result.SpoolerJobId,result.ErrorMessage,ct:ct);await QueueReportAsync(job,"unknown",result.SpoolerJobId,false,result.ErrorCode,result.ErrorMessage,ct);}
        else{await _store.SetStateAsync(job.AttemptId,LocalJobState.RecoveryHold,result.SpoolerJobId,result.ErrorMessage,ct:ct);await QueueReportAsync(job,"recovery_hold",result.SpoolerJobId,false,result.ErrorCode??"worker_recovery_hold",result.ErrorMessage,ct);}
    }
    private async Task QueueReportAsync(LocalJob job,string status,string? spooler,bool retryable,string? code,string? message,CancellationToken ct)
    {
        if(await _store.HasPendingReportAsync(job.AttemptId,ct))return;var requestId=CryptoUtil.NewRequestId();var body=JsonSerializer.Serialize(new{status,spooler_job_id=spooler,retryable,error_code=code,error_message=message},AgentOptions.JsonOptions());await _store.EnqueueReportAsync(job.ServerJobId,job.AttemptId,requestId,body,ct);var state=status=="unknown"?LocalJobState.Unknown:status=="recovery_hold"?LocalJobState.RecoveryHold:LocalJobState.ReportPending;await _store.SetStateAsync(job.AttemptId,state,spooler,message,ct:ct);
    }
    private async Task FlushReportsAsync(CancellationToken ct)
    {
        if(_api is null)return;foreach(var row in await _store.PendingReportsAsync(20,ct))
        {
            var job=await _store.GetByAttemptAsync(row.AttemptId,ct);if(job is null){await _store.MarkReportErrorAsync(row.Id,"Local attempt برای report پیدا نشد.",true,ct);continue;}
            try
            {
                using var doc=JsonDocument.Parse(row.BodyJson);var root=doc.RootElement;var status=root.GetProperty("status").GetString()??"recovery_hold";var spooler=root.TryGetProperty("spooler_job_id",out var sp)&&sp.ValueKind==JsonValueKind.String?sp.GetString():null;var retryable=root.TryGetProperty("retryable",out var rr)&&rr.ValueKind==JsonValueKind.True;var code=root.TryGetProperty("error_code",out var ec)&&ec.ValueKind==JsonValueKind.String?ec.GetString():null;var msg=root.TryGetProperty("error_message",out var em)&&em.ValueKind==JsonValueKind.String?em.GetString():null;var result=await RunApiAsync("report",()=>_api.ReportAsync(job,row.RequestId,status,spooler,retryable,code,msg,ct));if(result.Success)await _store.MarkReportSentAsync(row.Id,row.AttemptId,ct);
            }
            catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException api)
            {
                var permanent=(int)api.HttpStatus is >=400 and <500 && api.HttpStatus!=(HttpStatusCode)408 && api.HttpStatus!=(HttpStatusCode)429;await _store.MarkReportErrorAsync(row.Id,Safe(api.Message),permanent,ct);if(!permanent)break;
            }
            catch(ApiOperationException e){await _store.MarkReportErrorAsync(row.Id,Safe(e.InnerException?.Message??e.Message),false,ct);break;}
            catch(Exception e){await _store.MarkReportErrorAsync(row.Id,Safe(e.Message),false,ct);}
        }
    }

    private async Task MaybeHeartbeatAsync(CancellationToken ct)
    {
        if(_api is null||DateTimeOffset.UtcNow<_nextHeartbeat)return;var queues=SafeQueues();_printerDiscoveryAt=DateTimeOffset.UtcNow;var pairing=_options.LocalBridgeEnabled?LocalBridgeService.GetOrCreatePairingId(_paths):null;var origin=ResolveBridgeOrigin();var payload=new HeartbeatPayload(CryptoUtil.NewRequestId(),Environment.MachineName,AgentVersion,Environment.OSVersion.VersionString,(long)(DateTimeOffset.UtcNow-_started).TotalSeconds,_lastPoll?.ToString("O"),await _store.CountOpenAsync(ct),await _store.CountAmbiguousAsync(ct),_lastSubmission?.ToString("O"),"ok",DiskFreeMb(),File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"))),true,true,queues.ToList(),_lastSuccessfulAction,_lastApiSuccess?.ToString("O"),_lastApiErrorCode,_consecutiveApiFailures,_lastApiLatencyMs,_printerDiscoveryAt?.ToString("O"),_options.LocalBridgeEnabled?1:0,_options.LocalBridgeEnabled?_options.LocalBridgePort:0,pairing,origin);
        try{await RunApiAsync("heartbeat",()=>_api.HeartbeatAsync(payload,ct));_nextHeartbeat=DateTimeOffset.UtcNow.AddSeconds(_options.HeartbeatSeconds);}catch(ApiOperationException){_nextHeartbeat=DateTimeOffset.UtcNow.AddSeconds(Math.Max(10,_options.HeartbeatSeconds));}
    }
    private string? ResolveBridgeOrigin(){var raw=string.IsNullOrWhiteSpace(_options.LocalBridgeAllowedOrigin)?_options.ServerBaseUrl:_options.LocalBridgeAllowedOrigin;if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri))return null;return new UriBuilder(uri.Scheme,uri.Host,uri.IsDefaultPort?-1:uri.Port).Uri.GetLeftPart(UriPartial.Authority);}
    private async Task<T> RunApiAsync<T>(string action,Func<Task<T>> call){var sw=Stopwatch.StartNew();try{var result=await call();sw.Stop();_lastSuccessfulAction=action;_lastApiSuccess=DateTimeOffset.UtcNow;_lastApiLatencyMs=sw.ElapsedMilliseconds;_consecutiveApiFailures=0;return result;}catch(Exception e){sw.Stop();_lastApiLatencyMs=sw.ElapsedMilliseconds;_lastApiErrorCode=e is PrintApiException api&&!string.IsNullOrWhiteSpace(api.Code)?api.Code:e.GetType().Name;_consecutiveApiFailures++;LogSafe(action,e);throw new ApiOperationException(action,e);}}
    private async Task<string> StableRequestIdAsync(string key,CancellationToken ct){var existing=await _store.GetMetaAsync(key,ct);if(!string.IsNullOrWhiteSpace(existing))return existing;var created=CryptoUtil.NewRequestId();await _store.SetMetaAsync(key,created,ct);return created;}
    private async Task WriteLocalHealthAsync(string state,bool configOk,bool secretOk,string? error,CancellationToken ct){try{var serviceAccount=OperatingSystem.IsWindows()&&WindowsIdentity.GetCurrent().IsSystem;var snapshot=new LocalHealthSnapshot(AgentVersion,Environment.MachineName,state,configOk,secretOk,serviceAccount,error,DateTimeOffset.UtcNow.ToString("O"),await _store.CountOpenAsync(ct),await _store.CountAmbiguousAsync(ct),SafeQueues().ToList(),_lastSuccessfulAction,_lastApiSuccess?.ToString("O"),_lastApiErrorCode,_consecutiveApiFailures,_lastApiLatencyMs);await DurableFile.WriteJsonAtomicAsync(_paths.HealthPath,snapshot,ct);}catch(Exception e){_log.LogWarning("health.json: {Type}: {Message}",e.GetType().Name,Safe(e.Message));}}
    private IReadOnlyList<PrinterQueueHealth> SafeQueues(){try{return _printers.GetQueues();}catch(Exception e){LogSafe("printer_health",e);return[];}}
    private static bool QueueReady(PrinterQueueHealth p,string queue)=>string.Equals(p.Name,queue,StringComparison.OrdinalIgnoreCase)&&!p.Offline&&!p.Paused&&!p.PaperOut&&!p.Error;
    private long DiskFreeMb(){try{var root=Path.GetPathRoot(_paths.ProgramDataRoot);return root is null?0:new DriveInfo(root).AvailableFreeSpace/1024/1024;}catch{return 0;}}
    private string InputPath(LocalJob j)=>Path.Combine(_paths.WorkPath,$"input-{j.ServerJobId}-{j.AttemptId}.json");private string ResultPath(LocalJob j)=>Path.Combine(_paths.WorkPath,$"result-{j.ServerJobId}-{j.AttemptId}.json");private string FencePath(LocalJob j)=>Path.Combine(_paths.WorkPath,$"fence-{j.ServerJobId}-{j.AttemptId}.dat");private string StartSignalPath(LocalJob j)=>Path.Combine(_paths.WorkPath,$"start-{j.ServerJobId}-{j.AttemptId}.dat");
    private void CleanupTransientBeforeLaunch(LocalJob j){TryDelete(InputPath(j));TryDelete(ResultPath(j));TryDelete(FencePath(j));TryDelete(StartSignalPath(j));}private void CleanupWorkerFiles(LocalJob j){TryDelete(InputPath(j));TryDelete(ResultPath(j));TryDelete(FencePath(j));TryDelete(StartSignalPath(j));}
    private void LogSafe(string area,Exception e){_log.LogError("{Area}: {Type}: {Message}",area,e.GetType().Name,Safe(e.Message));try{_fileLog.Error(area,e);}catch(Exception logError){_log.LogError("FileLog: {Type}: {Message}",logError.GetType().Name,Safe(logError.Message));}}
    private static string Safe(string s)=>s.Length>400?s[..400]:s;private static void TryDelete(string p){try{if(File.Exists(p))File.Delete(p);}catch{}}private static void TryKill(Process p){try{if(!p.HasExited)p.Kill(true);}catch{}}
    private sealed class ApiOperationException:Exception{public string Action{get;}public ApiOperationException(string action,Exception inner):base($"Print API {action} failed.",inner)=>Action=action;}
}
