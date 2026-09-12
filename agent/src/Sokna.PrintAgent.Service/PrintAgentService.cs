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
    private const string PendingClaimMetaKey="pending_claim_v1"; // legacy read-only migration key
    private const string PendingClaimStateMetaKey="pending_claim_state_v2";
    private const string ServerScopeBindingMetaKey="server_scope_binding_v1";
    private const string LegacyServerScope="legacy-unbound";

    private static readonly TimeSpan ReportIoBudget=TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatIoBudget=TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DestinationRefreshIoBudget=TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EmptyReportInterval=TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActiveReportInterval=TimeSpan.FromMilliseconds(500);

    private readonly AgentPaths _paths;
    private readonly LocalQueueStore _store;
    private readonly IPrinterHealthReader _printers;
    private readonly ILogger<PrintAgentService> _log;
    private readonly AgentLog _fileLog;
    private readonly PrintWakeSignal _wake;
    private readonly ReportDispatcher _reports;
    private readonly DurableMutationRequestStore _mutationRequests;
    private readonly BridgeRuntimeState _bridgeRuntime;
    private readonly WorkerSupervisor _workerSupervisor;
    private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
    private readonly Mutex _mutex=new(false,@"Global\SoknaPrintAgentV6Service");
    private readonly SemaphoreSlim _coordinatorGate=new(1,1);
    private readonly SingleFlightWork<ReportDispatchSummary> _reportWork=new();
    private readonly SingleFlightWork<ApiResult> _heartbeatWork=new();
    private readonly SingleFlightWork<ProbeResponse> _destinationRefreshWork=new();

    private AgentOptions _options=new();
    private IPrintTransport? _api;
    private HttpClient? _http;
    private DateTime _configStampUtc=DateTime.MinValue;
    private DateTime _secretStampUtc=DateTime.MinValue;
    private DateTimeOffset? _lastPoll;
    private DateTimeOffset? _lastSubmission;
    private DateTimeOffset? _lastApiSuccess;
    private string? _lastSuccessfulAction;
    private string? _lastApiErrorCode;
    private int _consecutiveApiFailures;
    private long? _lastApiLatencyMs;
    private DateTimeOffset? _lastCoordinatorSuccess;
    private string? _lastCoordinatorErrorCode;
    private bool _claimReconciliationRequired;
    private int _claimConflictCount;
    private DateTimeOffset? _oldestClaimConflictAt;
    private long? _claimConflictAttemptId;
    private long? _claimConflictServerJobId;
    private string[]? _claimConflictFields;
    private string? _claimConflictServerScope;
    private DateTimeOffset _nextReportDispatch=DateTimeOffset.MinValue;
    private DateTimeOffset _nextHeartbeat=DateTimeOffset.MinValue;
    private DateTimeOffset _nextDestinationRefresh=DateTimeOffset.MinValue;
    private IReadOnlyList<DestinationConfig> _destinations=[];
    private string _serverScope=LegacyServerScope;
    private string _boundServerScope=LegacyServerScope;
    private bool _attemptStatusSupported;
    private bool _claimConflictRekeySupported;
    private long _configurationGeneration;
    private long _reportWorkGeneration;
    private long _heartbeatWorkGeneration;
    private long _destinationRefreshWorkGeneration;

    public PrintAgentService(
        AgentPaths paths,
        LocalQueueStore store,
        IPrinterHealthReader printers,
        ILogger<PrintAgentService> log,
        AgentLog fileLog,
        PrintWakeSignal wake,
        ReportDispatcher reports,
        DurableMutationRequestStore mutationRequests,
        BridgeRuntimeState bridgeRuntime,
        WorkerSupervisor workerSupervisor)
    {
        _paths=paths;
        _store=store;
        _printers=printers;
        _log=log;
        _fileLog=fileLog;
        _wake=wake;
        _reports=reports;
        _mutationRequests=mutationRequests;
        _bridgeRuntime=bridgeRuntime;
        _workerSupervisor=workerSupervisor;
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

                    ObserveSideIoResults();
                    StartSideIo(stoppingToken);

                    var cycle=await RunCoordinatorWorkAsync(stoppingToken);
                    await WriteLocalHealthAsync(
                        _claimReconciliationRequired?"reconciliation_required":_consecutiveApiFailures>0?"degraded":"running",
                        true,
                        true,
                        null,
                        stoppingToken);

                    // A report may have been created by the just-completed Worker. Start its delivery
                    // without placing any network wait in front of the next coordinator iteration.
                    StartSideIo(stoppingToken);

                    await _wake.WaitOrDelayAsync(
                        TimeSpan.FromMilliseconds(cycle.Processed||cycle.Claimed?_options.ActivePollMilliseconds:_options.IdlePollMilliseconds),
                        stoppingToken);
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch(ApiOperationException e)
                {
                    await WriteLocalHealthAsync(
                        "degraded",
                        _api is not null,
                        File.Exists(_paths.SecretPath),
                        Safe(e.InnerException?.Message??e.Message),
                        stoppingToken);
                    await _wake.WaitOrDelayAsync(TimeSpan.FromSeconds(3),stoppingToken);
                }
                catch(Exception e)
                {
                    LogSafe("loop",e);
                    await WriteLocalHealthAsync(
                        "degraded",
                        _api is not null,
                        File.Exists(_paths.SecretPath),
                        Safe(e.Message),
                        stoppingToken);
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

    /// <summary>
    /// Single owner for Claim/Accept/Start/Worker decisions. Wake/Poll only cause another call to
    /// this owner; they do not execute a second print path. The zero-time gate is also a defensive
    /// boundary for tests/future callers that accidentally try to run two iterations concurrently.
    /// </summary>
    private async Task<CoordinatorCycleResult> RunCoordinatorWorkAsync(CancellationToken ct)
    {
        if(!await _coordinatorGate.WaitAsync(0,ct))return new(false,false,false);
        try
        {
            await AcceptAnyReservedAsync(ct);
            await PromoteServerScopeWhenLegacyBacklogClearsAsync(ct);

            var processed=await ProcessOneAsync(ct);
            var claimed=false;
            if(!processed)
            {
                claimed=await ClaimAsync(ct);
                if(claimed)
                {
                    // No idle/poll delay is permitted between a successful Claim and its
                    // Accept/Process pass.
                    await AcceptAnyReservedAsync(ct);
                    processed=await ProcessOneAsync(ct);
                }
            }

            _lastPoll=DateTimeOffset.UtcNow;
            if(!_claimReconciliationRequired)
            {
                _lastCoordinatorSuccess=DateTimeOffset.UtcNow;
                _lastCoordinatorErrorCode=null;
            }
            return new(true,processed,claimed);
        }
        catch(Exception e)
        {
            if(!_claimReconciliationRequired)_lastCoordinatorErrorCode=e.GetType().Name;
            throw;
        }
        finally
        {
            _coordinatorGate.Release();
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

            candidateHttp=new HttpClient{Timeout=TimeSpan.FromSeconds(8)};
            var candidateApi=new HttpPrintTransport(candidateHttp,options.ServerBaseUrl,token);
            var probe=await RunApiAsync("probe_config",()=>candidateApi.ProbeAsync(ct));
            ValidateProbe(probe);
            var boundScope=await ResolveBoundServerScopeAsync(options,probe,ct);
            var legacyBacklog=(await _store.GetRecoverableAsync(ct)).Any(x=>string.Equals(x.ServerScope,LegacyServerScope,StringComparison.Ordinal));

            _http?.Dispose();
            _http=candidateHttp;
            candidateHttp=null;
            _api=candidateApi;
            _options=options;
            _destinations=probe.Destinations;
            _boundServerScope=boundScope;
            _serverScope=legacyBacklog?LegacyServerScope:boundScope;
            _attemptStatusSupported=ServerScopeResolver.Supports(probe,"attempt_status");
            _claimConflictRekeySupported=ServerScopeResolver.Supports(probe,"claim_conflict_rekey_v1");
            _configStampUtc=configStamp;
            _secretStampUtc=secretStamp;
            _configurationGeneration++;
            _nextReportDispatch=DateTimeOffset.MinValue;
            _nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(30);
            _nextHeartbeat=DateTimeOffset.MinValue;

            // A successful authenticated probe is the release condition for reports blocked by the same server identity.
            await _reports.ResumeAfterCredentialProbeAsync(_serverScope,ct);
            _fileLog.Info("configured",$"Print API v4 فعال شد؛ {probe.Destinations.Count} مقصد دریافت شد؛ scope={ScopeLabel(_serverScope)}.");
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

    private static void ValidateProbe(ProbeResponse probe)
    {
        if(!probe.Success||probe.ProtocolVersion!=4)throw new InvalidOperationException("Print API v4 آماده نیست.");
        if(!string.IsNullOrWhiteSpace(probe.ServerTime)&&!HasExplicitOffset(probe.ServerTime))
            throw new InvalidDataException("server_time باید ISO-8601 با offset صریح باشد.");
    }

    private async Task<string> ResolveBoundServerScopeAsync(AgentOptions options,ProbeResponse probe,CancellationToken ct)
    {
        var candidate=ServerScopeResolver.Resolve(options.ServerBaseUrl,probe.ServerInstanceId);
        var existing=await _store.GetMetaAsync(ServerScopeBindingMetaKey,ct);
        if(string.IsNullOrWhiteSpace(existing))
        {
            await _store.SetMetaAsync(ServerScopeBindingMetaKey,candidate,ct);
            return candidate;
        }
        if(!string.Equals(existing,candidate,StringComparison.Ordinal))
            throw new InvalidDataException("هویت ServerBaseUrl/ServerInstance با نصب bind‌شده متفاوت است؛ backlog خودکار به سامانهٔ دیگر ارسال نمی‌شود و reconciliation لازم است.");
        return existing;
    }

    private async Task PromoteServerScopeWhenLegacyBacklogClearsAsync(CancellationToken ct)
    {
        if(!string.Equals(_serverScope,LegacyServerScope,StringComparison.Ordinal)||string.Equals(_boundServerScope,LegacyServerScope,StringComparison.Ordinal))return;
        var legacyOpen=(await _store.GetRecoverableAsync(ct)).Any(x=>string.Equals(x.ServerScope,LegacyServerScope,StringComparison.Ordinal));
        if(legacyOpen)return;
        _serverScope=_boundServerScope;
        await _reports.ResumeAfterCredentialProbeAsync(_serverScope,ct);
        _fileLog.Info("server_scope_promoted",$"Legacy backlog پایان یافت؛ scope فعال={ScopeLabel(_serverScope)}.");
    }

    private void ObserveSideIoResults()
    {
        if(_reportWork.TryTakeCompleted(out var reportResult)&&reportResult is not null)
        {
            if(_reportWorkGeneration==_configurationGeneration)
            {
                if(reportResult.Error is not null)
                {
                    RecordSideFailure("report",reportResult.Error,reportResult.ElapsedMilliseconds);
                    _nextReportDispatch=DateTimeOffset.UtcNow.AddSeconds(2);
                }
                else if(reportResult.Value is { } summary)
                {
                    _lastApiLatencyMs=reportResult.ElapsedMilliseconds;
                    if(summary.LastErrorCode is not null)
                    {
                        _lastApiErrorCode=summary.LastErrorCode;
                        _consecutiveApiFailures++;
                    }
                    else if(summary.Delivered>0)
                    {
                        RecordSideSuccess("report",reportResult.ElapsedMilliseconds);
                        _fileLog.Info("report_ack",$"count={summary.Delivered}");
                    }
                    _nextReportDispatch=DateTimeOffset.UtcNow+(summary.Attempted==0?EmptyReportInterval:ActiveReportInterval);
                }
            }
        }

        if(_heartbeatWork.TryTakeCompleted(out var heartbeatResult)&&heartbeatResult is not null)
        {
            if(_heartbeatWorkGeneration==_configurationGeneration)
            {
                if(heartbeatResult.Error is not null)
                {
                    RecordSideFailure("heartbeat",heartbeatResult.Error,heartbeatResult.ElapsedMilliseconds);
                    _nextHeartbeat=DateTimeOffset.UtcNow.AddSeconds(Math.Max(10,_options.HeartbeatSeconds));
                }
                else
                {
                    RecordSideSuccess("heartbeat",heartbeatResult.ElapsedMilliseconds);
                    _nextHeartbeat=DateTimeOffset.UtcNow.AddSeconds(_options.HeartbeatSeconds);
                }
            }
        }

        if(_destinationRefreshWork.TryTakeCompleted(out var refreshResult)&&refreshResult is not null)
        {
            if(_destinationRefreshWorkGeneration==_configurationGeneration)
            {
                try
                {
                    if(refreshResult.Error is not null)throw refreshResult.Error;
                    var probe=refreshResult.Value??throw new InvalidDataException("probe_refresh پاسخ خالی داد.");
                    ValidateProbe(probe);
                    var scope=ServerScopeResolver.Resolve(_options.ServerBaseUrl,probe.ServerInstanceId);
                    if(!string.Equals(scope,_boundServerScope,StringComparison.Ordinal))
                        throw new InvalidDataException("هویت server در probe_refresh تغییر کرده است؛ ادامه خودکار متوقف شد.");
                    _destinations=probe.Destinations;
                    _attemptStatusSupported=ServerScopeResolver.Supports(probe,"attempt_status");
                    _claimConflictRekeySupported=ServerScopeResolver.Supports(probe,"claim_conflict_rekey_v1");
                    RecordSideSuccess("probe_refresh",refreshResult.ElapsedMilliseconds);
                    _nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(30);
                }
                catch(Exception e)
                {
                    RecordSideFailure("probe_refresh",e,refreshResult.ElapsedMilliseconds);
                    _nextDestinationRefresh=DateTimeOffset.UtcNow.AddSeconds(15);
                }
            }
        }
    }

    private void StartSideIo(CancellationToken ct)
    {
        var api=_api;
        if(api is null)return;
        var generation=_configurationGeneration;
        var now=DateTimeOffset.UtcNow;

        if(now>=_nextReportDispatch)
        {
            var scope=_serverScope;
            if(_reportWork.TryStart(
                token=>RunWithBudgetAsync(t=>_reports.DispatchBatchAsync(api,scope,20,t),ReportIoBudget,token),
                ct,
                _wake.Pulse))
            {
                _reportWorkGeneration=generation;
            }
        }

        if(now>=_nextHeartbeat)
        {
            var seed=CaptureHeartbeatSeed(generation);
            if(_heartbeatWork.TryStart(
                token=>RunWithBudgetAsync(t=>SendHeartbeatAsync(api,seed,t),HeartbeatIoBudget,token),
                ct,
                _wake.Pulse))
            {
                _heartbeatWorkGeneration=generation;
            }
        }

        if(now>=_nextDestinationRefresh)
        {
            if(_destinationRefreshWork.TryStart(
                token=>RunWithBudgetAsync(api.ProbeAsync,DestinationRefreshIoBudget,token),
                ct,
                _wake.Pulse))
            {
                _destinationRefreshWorkGeneration=generation;
            }
        }
    }

    private HeartbeatSeed CaptureHeartbeatSeed(long generation)
    {
        return new HeartbeatSeed(
            generation,
            (long)(DateTimeOffset.UtcNow-_started).TotalSeconds,
            _lastPoll?.ToString("O"),
            _lastSubmission?.ToString("O"),
            _lastSuccessfulAction,
            _lastApiSuccess?.ToString("O"),
            _lastApiErrorCode,
            _consecutiveApiFailures,
            _lastApiLatencyMs,
            ReadPrinterHealth(),
            BridgeHeartbeatProjection.From(_bridgeRuntime.Snapshot),
            DiskFreeMb(),
            File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"))));
    }

    private async Task<ApiResult> SendHeartbeatAsync(IPrintTransport api,HeartbeatSeed seed,CancellationToken ct)
    {
        var reportCounts=await _store.GetReportStateCountsAsync(ct);
        var payload=new HeartbeatPayload(
            CryptoUtil.NewRequestId(),
            Environment.MachineName,
            AgentVersion,
            Environment.OSVersion.VersionString,
            seed.UptimeSeconds,
            seed.LastPollSuccessAt,
            await _store.CountOpenAsync(ct),
            await _store.CountAmbiguousAsync(ct),
            seed.LastSubmissionAt,
            "ok",
            seed.DiskFreeMb,
            seed.WorkerOk,
            true,
            true,
            seed.PrinterHealth.Queues.ToList(),
            seed.LastSuccessfulAction,
            seed.LastApiSuccessAt,
            seed.LastApiErrorCode,
            seed.ConsecutiveApiFailures,
            seed.LastApiLatencyMs,
            seed.PrinterHealth.LastSuccessAt?.ToString("O"),
            seed.Bridge.ProtocolVersion,
            seed.Bridge.Port,
            seed.Bridge.PairingId,
            seed.Bridge.Origin,
            reportCounts.Pending+reportCounts.Backoff,
            reportCounts.AuthBlocked,
            reportCounts.ReconciliationRequired,
            seed.PrinterHealth.LastFailureAt?.ToString("O"),
            seed.PrinterHealth.LastError,
            seed.PrinterHealth.AgeMilliseconds,
            seed.PrinterHealth.IsFresh,
            seed.PrinterHealth.Generation);
        return await api.HeartbeatAsync(payload,ct);
    }

    private static async Task<T> RunWithBudgetAsync<T>(Func<CancellationToken,Task<T>> call,TimeSpan budget,CancellationToken serviceToken)
    {
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        bounded.CancelAfter(budget);
        return await call(bounded.Token);
    }

    private void RecordSideSuccess(string action,long elapsedMilliseconds)
    {
        _lastSuccessfulAction=action;
        _lastApiSuccess=DateTimeOffset.UtcNow;
        _lastApiErrorCode=null;
        _lastApiLatencyMs=elapsedMilliseconds;
        _consecutiveApiFailures=0;
    }

    private void RecordSideFailure(string action,Exception error,long elapsedMilliseconds)
    {
        _lastApiLatencyMs=elapsedMilliseconds;
        _lastApiErrorCode=error is PrintApiException api&&!string.IsNullOrWhiteSpace(api.Code)?api.Code:error.GetType().Name;
        _consecutiveApiFailures++;
        if(error is OperationCanceledException)
        {
            _fileLog.Info(action,$"side I/O budget exceeded; elapsed_ms={elapsedMilliseconds}");
            return;
        }
        LogSafe(action,error);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        foreach(var job in await _store.GetRecoverableAsync(ct))
        {
            var outcome=await _store.GetOutcomeAsync(job.AttemptId,ct);
            if(outcome is not null)continue;

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
                    await PersistOutcomeAndReportAsync(
                        job,PrintOutcomeStatus.RecoveryHold,null,false,
                        "service_restart_after_submission_fence",
                        "Service پس از Submission Fence بازیابی شد و نتیجه قابل اثبات نیست.",
                        "recovery:fence",ct);
                }
                else
                {
                    await PersistOutcomeAndReportAsync(
                        job,PrintOutcomeStatus.RecoveryHold,null,false,
                        "service_restart_worker_state_ambiguous",
                        "WorkerLaunching پس از restart بدون شواهد قطعی مرگ child بازیابی شد؛ چاپ مجدد خودکار ممنوع است.",
                        "recovery:worker-launch",ct);
                }
                continue;
            }

            switch(job.State)
            {
                case LocalJobState.Submitted when !string.IsNullOrWhiteSpace(job.SpoolerJobId):
                    await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.Submitted,job.SpoolerJobId,false,null,null,"recovery:legacy-submitted",ct);
                    break;
                case LocalJobState.ReportPending:
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
        var pendingState=await LoadPendingClaimStateAsync(ct);
        if(pendingState?.IsQuarantined==true)
        {
            if(!await TryResolveClaimQuarantineAsync(pendingState,ct))return false;
            pendingState=await LoadPendingClaimStateAsync(ct);
        }

        if(pendingState is null)
        {
            var health=ReadyQueues();
            var ready=_destinations
                .Where(d=>health.Any(p=>QueueReady(p,d.WindowsQueueName)))
                .Select(d=>d.DestinationKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if(ready.Length==0)return false;

            var request=new ClaimRequestEnvelope(
                CryptoUtil.NewRequestId(),
                AgentVersion,
                4,
                ready,
                _options.ClaimBatchSize,
                DateTimeOffset.UtcNow.ToString("O"));
            pendingState=PendingClaimState.Active(request);
            await SavePendingClaimStateAsync(pendingState,ct);
        }

        var pending=pendingState.Request;
        _fileLog.Info("claim_started",$"request={ShortId(pending.RequestId)}; destinations={pending.ReadyDestinationKeys.Length}");
        var response=await RunApiAsync("claim",()=>_api.ClaimAsync(pending,ct));
        ValidateClaimResponse(response,pending);
        foreach(var item in response.Jobs)
        {
            var persistence=await _store.PersistReservedResultAsync(item,CryptoUtil.NewLocalReceiptId(),_serverScope,ct);
            if(persistence.Disposition!=ClaimPersistenceDisposition.ReconciliationRequired)continue;

            var now=DateTimeOffset.UtcNow;
            var conflictFields=persistence.MismatchedFields.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            pendingState=pendingState with
            {
                State="quarantined",
                QuarantinedAt=pendingState.QuarantinedAt??now.ToString("O"),
                AttemptId=item.Attempt.Id,
                ServerJobId=item.Job.Id,
                ConflictFields=conflictFields,
                ConflictCount=Math.Max(1,pendingState.ConflictCount+1),
                LastConflictAt=now.ToString("O"),
                ServerScopeLabel=ScopeLabel(_serverScope),
                ResolutionRequestId=pendingState.ResolutionRequestId??CryptoUtil.NewRequestId()
            };
            await SavePendingClaimStateAsync(pendingState,ct);
            ApplyClaimQuarantineHealth(pendingState);
            _fileLog.Error("claim_reconciliation_required",new InvalidDataException($"attempt={item.Attempt.Id}; fields={string.Join(',',conflictFields)}; scope={ScopeLabel(_serverScope)}"));
            return false;
        }

        await _store.DeleteMetaAsync(PendingClaimStateMetaKey,ct);
        await _store.DeleteMetaAsync(PendingClaimMetaKey,ct);
        _claimReconciliationRequired=false;
        _lastCoordinatorErrorCode=null;
        _fileLog.Info("claim_completed",$"request={ShortId(pending.RequestId)}; jobs={response.Jobs.Count}");
        return response.Jobs.Count>0;
    }

    private static void ValidateClaimResponse(ClaimResponse response,ClaimRequestEnvelope request)
    {
        if(!response.Success)throw new InvalidDataException("Claim response success=false است.");
        if(!string.Equals(response.RequestId,request.RequestId,StringComparison.Ordinal))throw new InvalidDataException("Claim response request_id mismatch.");
        if(!HasExplicitOffset(response.ServerTime))throw new InvalidDataException("Claim server_time باید offset صریح داشته باشد.");
    }

    private async Task<PendingClaimState?> LoadPendingClaimStateAsync(CancellationToken ct)
    {
        var raw=await _store.GetMetaAsync(PendingClaimStateMetaKey,ct);
        if(!string.IsNullOrWhiteSpace(raw))
        {
            var state=JsonSerializer.Deserialize<PendingClaimState>(raw,AgentOptions.JsonOptions())
                ?? throw new InvalidDataException("Pending claim state نامعتبر است.");
            if(state.Version==2){state=state with{Version=PendingClaimState.CurrentVersion};await SavePendingClaimStateAsync(state,ct);}
            ValidatePendingClaimState(state);
            return state;
        }

        var legacy=await _store.GetMetaAsync(PendingClaimMetaKey,ct);
        if(string.IsNullOrWhiteSpace(legacy))return null;
        try
        {
            var request=JsonSerializer.Deserialize<ClaimRequestEnvelope>(legacy,AgentOptions.JsonOptions())
                ?? throw new InvalidDataException("Pending claim metadata نامعتبر است.");
            ValidatePendingClaimRequest(request);
            var migrated=PendingClaimState.Active(request);
            await SavePendingClaimStateAsync(migrated,ct);
            await _store.DeleteMetaAsync(PendingClaimMetaKey,ct);
            return migrated;
        }
        catch(Exception e)
        {
            _lastCoordinatorErrorCode="pending_claim_metadata_invalid";
            LogSafe("claim_replay_metadata",e);
            throw; // fail closed: preserve durable evidence instead of inventing a new request_id
        }
    }

    private async Task SavePendingClaimStateAsync(PendingClaimState state,CancellationToken ct)
        =>await _store.SetMetaAsync(PendingClaimStateMetaKey,JsonSerializer.Serialize(state,AgentOptions.JsonOptions()),ct);

    private static void ValidatePendingClaimState(PendingClaimState state)
    {
        if(state.Version!=PendingClaimState.CurrentVersion||state.State is not ("active" or "quarantined"))
            throw new InvalidDataException("Pending claim state version/state نامعتبر است.");
        ValidatePendingClaimRequest(state.Request);
        if(state.IsQuarantined&&(state.AttemptId is null||state.ConflictFields is null||state.ConflictFields.Length==0||!HasExplicitOffset(state.QuarantinedAt??"")))
            throw new InvalidDataException("Quarantined claim metadata ناقص است.");
    }

    private static void ValidatePendingClaimRequest(ClaimRequestEnvelope pending)
    {
        if(string.IsNullOrWhiteSpace(pending.RequestId)||pending.ProtocolVersion!=4||pending.ReadyDestinationKeys.Length==0||pending.Limit is <1 or >5||!HasExplicitOffset(pending.CreatedAt))
            throw new InvalidDataException("Pending claim metadata نامعتبر است.");
    }

    private void ApplyClaimQuarantineHealth(PendingClaimState state)
    {
        _claimReconciliationRequired=true;
        _lastCoordinatorErrorCode="claim_reconciliation_required";
        _claimConflictCount=Math.Max(_claimConflictCount,state.ConflictCount);
        _claimConflictAttemptId=state.AttemptId;
        _claimConflictServerJobId=state.ServerJobId;
        _claimConflictFields=state.ConflictFields;
        _claimConflictServerScope=state.ServerScopeLabel;
        if(DateTimeOffset.TryParse(state.QuarantinedAt,out var quarantinedAt))
            _oldestClaimConflictAt=_oldestClaimConflictAt is null||quarantinedAt<_oldestClaimConflictAt?quarantinedAt:_oldestClaimConflictAt;
    }

    private async Task<bool> TryResolveClaimQuarantineAsync(PendingClaimState state,CancellationToken ct)
    {
        ApplyClaimQuarantineHealth(state);
        if(!_claimConflictRekeySupported||_api is null||state.AttemptId is null)return false;
        var local=await _store.GetByAttemptAsync(state.AttemptId.Value,ct);
        if(local is null)return false;
        var resolutionId=state.ResolutionRequestId;
        if(string.IsNullOrWhiteSpace(resolutionId))
        {
            resolutionId=CryptoUtil.NewRequestId();
            state=state with{ResolutionRequestId=resolutionId};
            await SavePendingClaimStateAsync(state,ct);
        }
        var request=new ClaimConflictResolutionRequest(
            resolutionId,
            state.Request.RequestId,
            state.AttemptId.Value,
            local.ServerJobId,
            local.ContentSha256,
            local.DestinationKey,
            await _store.GetMaxAttemptIdAsync(ct),
            state.ConflictFields??[]);
        var result=await RunApiAsync("claim_reconcile",()=>_api.ResolveClaimConflictAsync(request,ct));
        if(!result.Success||!string.Equals(result.Status,"replacement_reserved",StringComparison.Ordinal)||result.OldAttemptId!=state.AttemptId||result.ReplacementAttemptId is null||result.ReplacementAttemptId<=request.LocalMaxAttemptId)
            throw new InvalidDataException("پاسخ claim_reconcile معتبر نیست؛ quarantine حفظ شد.");
        var active=PendingClaimState.Active(state.Request);
        await SavePendingClaimStateAsync(active,ct);
        _claimReconciliationRequired=false;
        _lastCoordinatorErrorCode=null;
        _claimConflictAttemptId=null;_claimConflictServerJobId=null;_claimConflictFields=null;_claimConflictServerScope=null;
        _fileLog.Info("claim_reconciliation_completed",$"old_attempt={state.AttemptId}; replacement_attempt={result.ReplacementAttemptId}; fields={string.Join(',',state.ConflictFields??[])}");
        return true;
    }

    private async Task AcceptAnyReservedAsync(CancellationToken ct)
    {
        if(_api is null)return;
        foreach(var local in (await _store.GetRecoverableAsync(ct)).Where(x=>x.State==LocalJobState.Reserved).OrderBy(x=>x.ServerJobId).ThenBy(x=>x.AttemptNo))
        {
            var request=await _mutationRequests.GetOrCreateAcceptAsync(local,ct);
            if(local.LeaseExpiresAt<=DateTimeOffset.UtcNow)
            {
                if(!_attemptStatusSupported)
                {
                    await _store.SetStateAsync(local.AttemptId,LocalJobState.RecoveryHold,error:"Server فاقد capability attempt_status برای بازیابی Accept مبهم پس از انقضای lease است؛ چاپ حدسی ممنوع است.",ct:ct);
                    continue;
                }
                try
                {
                    var status=await RunApiAsync("attempt_status",()=>_api.AttemptStatusAsync(local,ct));
                    if(!TryValidateAttemptStatus(status,local,out var validationError))
                    {
                        await _store.SetStateAsync(local.AttemptId,LocalJobState.RecoveryHold,error:validationError,ct:ct);
                        continue;
                    }
                    if(status.AttemptState is "claimed" or "started")
                    {
                        await _store.SetStateAsync(local.AttemptId,LocalJobState.Claimed,ct:ct);
                        await _mutationRequests.CompleteAcceptAsync(local.AttemptId,ct);
                        _fileLog.Info("accept_completed",$"job={local.ServerJobId}; attempt={local.AttemptId}; reconciled=1");
                        continue;
                    }
                    if(status.Terminal&&status.AttemptState is "expired" or "cancelled" or "failed")
                    {
                        await _store.SetStateAsync(local.AttemptId,LocalJobState.Resolved,error:$"Server state: {status.AttemptState}",ct:ct);
                        await _mutationRequests.CompleteAcceptAsync(local.AttemptId,ct);
                    }
                    continue;
                }
                catch(ApiOperationException){continue;}
            }

            var destination=new DestinationConfig(
                local.DestinationKey,
                local.DestinationKey,
                local.QueueName,
                local.PaperWidthMm,
                local.PrintableWidthMm,
                local.Copies,
                local.LayoutMode);
            var item=new ClaimItem(
                new((int)local.ServerJobId,"","",false,null,null,"",4,local.ContentSha256,local.PayloadJson),
                new(local.AttemptId,local.AttemptNo,SecretStore.UnprotectText(local.ProtectedLeaseToken),local.LeaseExpiresAt.ToString("O")),
                destination);
            try
            {
                var result=await RunApiAsync("accept",()=>_api.AcceptAsync(item,request,ct));
                if(!ValidateMutationResponse(result,local,"accept"))
                {
                    await _store.SetStateAsync(local.AttemptId,LocalJobState.RecoveryHold,error:"Accept response هویت/مجوز معتبر نداشت.",ct:ct);
                    continue;
                }
                await _store.SetStateAsync(local.AttemptId,LocalJobState.Claimed,ct:ct);
                await _mutationRequests.CompleteAcceptAsync(local.AttemptId,ct);
                _fileLog.Info("accept_completed",$"job={local.ServerJobId}; attempt={local.AttemptId}; reconciled=0");
            }
            catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException {Code:"lease_expired"})
            {
                // Same durable request will be reconciled on a later pass; no local expiry guess is made.
            }
        }
    }

    private static bool TryValidateAttemptStatus(AttemptStatusResult result,LocalJob local,out string error)
    {
        error="Attempt status نامعتبر است.";
        if(!result.Success){error="Attempt status success=false است.";return false;}
        if(result.AttemptId!=local.AttemptId||result.JobId!=local.ServerJobId){error="Attempt status identity mismatch است.";return false;}
        if(!result.ReceiptMatches){error="Attempt status receipt mismatch است.";return false;}
        if(result.RequiresHumanResolution){error="Attempt status نیازمند تعیین تکلیف انسانی است.";return false;}
        if(string.IsNullOrWhiteSpace(result.AttemptState)||string.IsNullOrWhiteSpace(result.NextAction)){error="Attempt status state/action ناقص است.";return false;}
        if(!HasExplicitOffset(result.ServerTime)){error="Attempt status server_time فاقد offset صریح است.";return false;}
        if(!string.IsNullOrWhiteSpace(result.LeaseExpiresAt)&&!HasExplicitOffset(result.LeaseExpiresAt)){error="Attempt status lease_expires_at فاقد offset صریح است.";return false;}
        if(result.AttemptState=="claimed"&&result.NextAction is not ("start" or "continue")){error="Attempt claimed با next_action ناسازگار است.";return false;}
        if(result.AttemptState=="started"&&result.NextAction is not ("continue" or "report" or "start")){error="Attempt started با next_action ناسازگار است.";return false;}
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
        if(!string.IsNullOrWhiteSpace(result.ServerTime)&&!HasExplicitOffset(result.ServerTime))return false;
        return true;
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        if(_api is null)return false;
        var queues=ReadyQueues();
        var open=await _store.GetRecoverableAsync(ct);
        var job=open
            .Where(x=>x.State==LocalJobState.Claimed)
            .OrderBy(x=>x.ServerJobId)
            .ThenBy(x=>x.AttemptNo)
            .FirstOrDefault(candidate=>
                queues.Any(q=>QueueReady(q,candidate.QueueName))&&
                !open.Any(older=>string.Equals(older.DestinationKey,candidate.DestinationKey,StringComparison.OrdinalIgnoreCase)&&older.ServerJobId<candidate.ServerJobId));
        if(job is null)return false;

        var startRequest=await _mutationRequests.GetOrCreateStartAsync(job,ct);
        try
        {
            var started=await RunApiAsync("start",()=>_api.StartAsync(job,startRequest,ct));
            if(!ValidateMutationResponse(started,job,"start"))
            {
                await PersistOutcomeAndReportAsync(job,PrintOutcomeStatus.RecoveryHold,null,false,"invalid_start_response","Start response هویت/مجوز معتبر نداشت.","server:start-validation",ct);
                return true;
            }
        }
        catch(ApiOperationException wrapped) when(wrapped.InnerException is PrintApiException {Terminal:true} e)
        {
            await _store.SetStateAsync(job.AttemptId,LocalJobState.Resolved,error:$"Server state: {e.CurrentState??"terminal"}",ct:ct);
            await _mutationRequests.CompleteStartAsync(job.AttemptId,ct);
            return true;
        }

        CleanupTransientBeforeLaunch(job);
        var input=new WorkerInput(
            job.ServerJobId,
            job.AttemptId,
            job.LocalReceiptId,
            job.QueueName,
            job.PayloadJson,
            job.ContentSha256,
            job.PaperWidthMm,
            job.PrintableWidthMm,
            job.Copies,
            ResultPath(job),
            FencePath(job),
            StartSignalPath(job));
        await DurableFile.WriteJsonAtomicAsync(InputPath(job),input,ct);
        await _store.SetStateAsync(job.AttemptId,LocalJobState.WorkerLaunching,markWorkerLaunching:true,ct:ct);
        await _mutationRequests.CompleteStartAsync(job.AttemptId,ct);

        var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));
        var spec=new WorkerLaunchSpec(
            worker,
            $"\"{InputPath(job)}\"",
            Path.GetDirectoryName(worker)!,
            TimeSpan.FromSeconds(_options.WorkerTimeoutSeconds),
            TimeSpan.FromMilliseconds(_options.WorkerExitProofTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(_options.WorkerShutdownExitProofTimeoutMilliseconds),
            4096);

        _fileLog.Info("worker_launch",$"job={job.ServerJobId}; attempt={job.AttemptId}; destination={Safe(job.DestinationKey)}");
        var supervised=await _workerSupervisor.RunAsync(
            spec,
            token=>DurableFile.TouchAtomicAsync(StartSignalPath(job),$"start:{job.AttemptId}",token),
            ct);
        var commitToken=ct.IsCancellationRequested?CancellationToken.None:ct;

        var durable=await TryReadWorkerResultAsync(job,commitToken);
        if(durable is not null)
        {
            await ApplyWorkerResultAsync(job,durable,commitToken);
            CleanupWorkerFiles(job);
            return true;
        }

        if(!supervised.ExitProven)
        {
            await PersistOutcomeAndReportAsync(
                job,
                PrintOutcomeStatus.RecoveryHold,
                null,
                false,
                "worker_exit_unproven",
                supervised.Error??"پایان child در deadline اثبات نشد؛ شواهد حفظ و Retry خودکار ممنوع است.",
                "supervisor:exit-unproven",
                CancellationToken.None);
            return true;
        }

        var stderr=string.IsNullOrWhiteSpace(supervised.StandardError)?supervised.Error:supervised.StandardError;
        if(File.Exists(FencePath(job)))
        {
            await PersistOutcomeAndReportAsync(
                job,
                PrintOutcomeStatus.RecoveryHold,
                null,
                false,
                WorkerStopCode(supervised.StopKind)+"_after_fence",
                string.IsNullOrWhiteSpace(stderr)?"Worker پس از Submission Fence بدون نتیجه قطعی پایان یافت؛ Retry خودکار ممنوع است.":stderr,
                "supervisor:fence",
                commitToken);
        }
        else
        {
            await PersistOutcomeAndReportAsync(
                job,
                PrintOutcomeStatus.Failed,
                null,
                true,
                WorkerStopCode(supervised.StopKind)+"_before_fence",
                string.IsNullOrWhiteSpace(stderr)?"پایان Worker پیش از Submission Fence اثبات شد؛ Retry ایمن مجاز است.":stderr,
                "supervisor:pre-fence",
                commitToken);
        }
        CleanupWorkerFiles(job);
        return true;
    }

    private static string WorkerStopCode(WorkerStopKind kind)=>kind switch
    {
        WorkerStopKind.LaunchFailed=>"worker_process_start_failed",
        WorkerStopKind.GuardFailed=>"worker_guard_failed",
        WorkerStopKind.StartSignalFailed=>"worker_start_signal_failed",
        WorkerStopKind.ExecutionTimeout=>"worker_timeout",
        WorkerStopKind.ServiceShutdown=>"worker_service_shutdown",
        WorkerStopKind.ExitUnproven=>"worker_exit_unproven",
        _=>"worker_exit_without_result"
    };

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
                _fileLog.Info("spooler_submitted",$"job={job.ServerJobId}; attempt={job.AttemptId}; spooler={Safe(result.SpoolerJobId)}");
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
        _nextReportDispatch=DateTimeOffset.MinValue;
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

    private async Task WriteLocalHealthAsync(string state,bool configOk,bool secretOk,string? error,CancellationToken ct)
    {
        try
        {
            var serviceAccount=OperatingSystem.IsWindows()&&WindowsIdentity.GetCurrent().IsSystem;
            var counts=await _store.GetReportStateCountsAsync(ct);
            var oldest=await _store.GetOldestUndeliveredReportAgeSecondsAsync(ct);
            var printerHealth=ReadPrinterHealth();
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
                printerHealth.Queues.ToList(),
                _lastSuccessfulAction,
                _lastApiSuccess?.ToString("O"),
                _lastApiErrorCode,
                _consecutiveApiFailures,
                _lastApiLatencyMs,
                counts.Pending,
                counts.Backoff,
                counts.AuthBlocked,
                counts.ReconciliationRequired,
                oldest,
                printerHealth.LastSuccessAt?.ToString("O"),
                printerHealth.LastFailureAt?.ToString("O"),
                printerHealth.LastError,
                printerHealth.AgeMilliseconds,
                printerHealth.IsFresh,
                printerHealth.Generation,
                _consecutiveApiFailures>0?"degraded":_lastApiSuccess is null?"unknown":"healthy",
                _lastApiSuccess?.ToString("O"),
                _lastApiErrorCode,
                _consecutiveApiFailures,
                _claimReconciliationRequired?"reconciliation_required":_lastCoordinatorErrorCode is null?(_lastCoordinatorSuccess is null?"unknown":"healthy"):"degraded",
                _lastCoordinatorSuccess?.ToString("O"),
                _lastCoordinatorErrorCode,
                _claimReconciliationRequired,
                _claimConflictCount,
                _oldestClaimConflictAt is null?null:(long)Math.Max(0,(DateTimeOffset.UtcNow-_oldestClaimConflictAt.Value).TotalSeconds),
                _claimConflictAttemptId,
                _claimConflictServerJobId,
                _claimConflictFields,
                _claimConflictServerScope);
            await DurableFile.WriteJsonAtomicAsync(_paths.HealthPath,snapshot,ct);
        }
        catch(Exception e)
        {
            _log.LogWarning("health.json: {Type}: {Message}",e.GetType().Name,Safe(e.Message));
        }
    }

    private PrinterHealthSnapshot ReadPrinterHealth()
    {
        try{return _printers.Read(PrinterDiscoveryService.FreshnessWindow);}
        catch(Exception e)
        {
            LogSafe("printer_health_cache",e);
            return PrinterHealthSnapshot.Unavailable(Safe(e.Message));
        }
    }

    private IReadOnlyList<PrinterQueueHealth> ReadyQueues()
    {
        var snapshot=ReadPrinterHealth();
        return snapshot.IsFresh?snapshot.Queues:[];
    }

    private static bool QueueReady(PrinterQueueHealth printer,string queue)
        =>string.Equals(printer.Name,queue,StringComparison.OrdinalIgnoreCase)&&!printer.Offline&&!printer.Paused&&!printer.PaperOut&&!printer.Error;

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

    private static bool HasExplicitOffset(string value)
    {
        if(string.IsNullOrWhiteSpace(value))return false;
        var text=value.Trim();
        var time=text.IndexOf('T');
        var offset=Math.Max(text.LastIndexOf('+'),text.LastIndexOf('-'));
        return (text.EndsWith('Z')||(offset>time&&offset>=0))&&DateTimeOffset.TryParse(text,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out _);
    }

    private static string ScopeLabel(string value)=>value.Length<=16?value:value[..16]+"…";
    private static string ShortId(string value)=>value.Length<=12?value:value[..12];
    private static string Safe(string value)=>SafeLogText.Sanitize(value,400);
    private static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}

    private sealed record CoordinatorCycleResult(bool OwnerAcquired,bool Processed,bool Claimed);

    private sealed record HeartbeatSeed(
        long Generation,
        long UptimeSeconds,
        string? LastPollSuccessAt,
        string? LastSubmissionAt,
        string? LastSuccessfulAction,
        string? LastApiSuccessAt,
        string? LastApiErrorCode,
        int ConsecutiveApiFailures,
        long? LastApiLatencyMs,
        PrinterHealthSnapshot PrinterHealth,
        BridgeHeartbeatFields Bridge,
        long DiskFreeMb,
        bool WorkerOk);

    private sealed class ApiOperationException : Exception
    {
        public string Action { get; }
        public ApiOperationException(string action,Exception inner):base($"Print API {action} failed.",inner)=>Action=action;
    }
}
