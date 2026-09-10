using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed record WorkerEvidenceSweepResult(int Scanned,int Deleted,int Retained,int Failed);

/// <summary>
/// Best-effort janitor for durable Worker IPC artifacts. The print path commits outcome/outbox first;
/// this component only removes evidence after a durable outcome exists, and retries cleanup failures
/// on later sweeps. Ambiguous evidence is retained for a bounded forensic window.
/// </summary>
public sealed class WorkerEvidenceJanitor : BackgroundService
{
    public static readonly TimeSpan DefaultAmbiguousRetention=TimeSpan.FromDays(30);
    private static readonly Regex ArtifactName=new(
        @"^(?:input|result|fence|start)-[0-9]+-(?<attempt>[0-9]+)\.(?:json|dat)$",
        RegexOptions.Compiled|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);

    private readonly AgentPaths _paths;
    private readonly LocalQueueStore _store;
    private readonly AgentLog _log;
    private readonly TimeSpan _ambiguousRetention;
    private readonly TimeSpan _sweepInterval;

    public WorkerEvidenceJanitor(AgentPaths paths,LocalQueueStore store,AgentLog log)
        :this(paths,store,log,DefaultAmbiguousRetention,TimeSpan.FromMinutes(1)){}

    public WorkerEvidenceJanitor(
        AgentPaths paths,
        LocalQueueStore store,
        AgentLog log,
        TimeSpan ambiguousRetention,
        TimeSpan sweepInterval)
    {
        if(ambiguousRetention<TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(ambiguousRetention));
        if(sweepInterval<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(sweepInterval));
        _paths=paths;
        _store=store;
        _log=log;
        _ambiguousRetention=ambiguousRetention;
        _sweepInterval=sweepInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PrintAgentService owns schema initialization. A short delay avoids competing with its startup migration.
        try{await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);}catch(OperationCanceledException){return;}
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result=await SweepOnceAsync(DateTimeOffset.UtcNow,stoppingToken);
                if(result.Failed>0)
                    _log.Warn("worker_evidence_cleanup",$"Cleanup retry pending for {result.Failed} Worker evidence file(s).");
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e)
            {
                try{_log.Warn("worker_evidence_cleanup",$"Sweep failed: {SafeLogText.Sanitize(e.Message,300)}");}catch{}
            }

            try{await Task.Delay(_sweepInterval,stoppingToken);}catch(OperationCanceledException){break;}
        }
    }

    public async Task<WorkerEvidenceSweepResult> SweepOnceAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        Directory.CreateDirectory(_paths.WorkPath);
        var scanned=0;
        var deleted=0;
        var retained=0;
        var failed=0;

        foreach(var path in Directory.EnumerateFiles(_paths.WorkPath,"*",SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var match=ArtifactName.Match(Path.GetFileName(path));
            if(!match.Success||!long.TryParse(match.Groups["attempt"].Value,out var attemptId))continue;
            scanned++;

            var job=await _store.GetByAttemptAsync(attemptId,ct);
            var outcome=await _store.GetOutcomeAsync(attemptId,ct);
            if(job is null||outcome is null)
            {
                // No durable classification yet: this can still be live recovery evidence.
                retained++;
                continue;
            }

            var eligible=outcome.Status switch
            {
                PrintOutcomeStatus.Submitted=>true,
                PrintOutcomeStatus.Failed=>true,
                PrintOutcomeStatus.Unknown or PrintOutcomeStatus.RecoveryHold=>now-outcome.CommittedAt>=_ambiguousRetention,
                _=>false
            };
            if(!eligible)
            {
                retained++;
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {
                failed++;
                try{_log.Warn("worker_evidence_cleanup",$"Delete deferred for {Path.GetFileName(path)}: {SafeLogText.Sanitize(e.Message,240)}");}catch{}
            }
        }

        return new(scanned,deleted,retained,failed);
    }
}
