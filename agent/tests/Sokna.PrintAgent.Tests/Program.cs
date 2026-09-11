using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Service;

var failures=new List<string>();
void Check(bool value,string name){if(!value)failures.Add(name);}
async Task ExpectThrowsAsync(Func<Task> action,string name){try{await action();failures.Add(name);}catch{}}

Check(RecoveryPolicy.Decide(LocalJobState.Reserved,false,false)==RecoveryDecision.ContinueAccept,"restart_before_accept");
Check(RecoveryPolicy.Decide(LocalJobState.Claimed,false,false)==RecoveryDecision.ContinueClaimed,"restart_after_accept");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,false,false)==RecoveryDecision.RecoveryHold,"restart_workerlaunching_without_death_proof_is_ambiguous");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,true,false)==RecoveryDecision.RecoveryHold,"crash_after_submission_fence_ambiguous");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,true,true)==RecoveryDecision.Nothing,"durable_worker_result_takes_precedence");
Check(RecoveryPolicy.Decide(LocalJobState.Submitted,true,true)==RecoveryDecision.ReportSubmitted,"crash_after_spooler_before_report_no_reprint");
Check(RecoveryPolicy.Decide(LocalJobState.Unknown,true,false)==RecoveryDecision.RetryReport,"unknown_retry_report_only");
Check(CryptoUtil.Sha256Hex("سلام").Length==64,"sha256");
Check(!string.IsNullOrWhiteSpace(AgentVersionInfo.Current),"agent_version_source_available");
Check(SafeLogText.Sanitize("Authorization: Bearer abc-raw-secret")=="[redacted-sensitive-text]","authorization_log_redacted");
Check(SafeLogText.Sanitize("{\"payload_json\":\"full-order\"}")=="[redacted-sensitive-text]","payload_log_redacted");
Check(SafeLogText.Sanitize("network timeout",7)=="network","safe_log_bounded");

var legacyEmptyDir=Path.Combine(Path.GetTempPath(),"sokna-agent-legacy-empty-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(legacyEmptyDir);
var legacyEmptyPath=Path.Combine(legacyEmptyDir,"queue.db");
await CreateLegacyQueueAsync(legacyEmptyPath,false);
var legacyPreparation=await QueueDatabaseBootstrap.PrepareAsync(legacyEmptyPath);
Check(legacyPreparation.ReinitializedLegacyEmptyDatabase,"legacy_empty_queue_reinitialized");
Check(!string.IsNullOrWhiteSpace(legacyPreparation.BackupPath)&&File.Exists(legacyPreparation.BackupPath),"legacy_empty_queue_backup_preserved");
Check(!File.Exists(legacyEmptyPath),"legacy_empty_queue_original_moved_before_recreate");
var legacyRecreated=new LocalQueueStore(legacyEmptyPath,new TestLeaseProtector());
await legacyRecreated.InitializeAsync();
Check(await legacyRecreated.GetMetaAsync("schema_version")=="4","legacy_empty_queue_recreated_current_schema");

var legacyBusyDir=Path.Combine(Path.GetTempPath(),"sokna-agent-legacy-busy-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(legacyBusyDir);
var legacyBusyPath=Path.Combine(legacyBusyDir,"queue.db");
await CreateLegacyQueueAsync(legacyBusyPath,true);
var legacyBusyBlocked=false;
try{await QueueDatabaseBootstrap.PrepareAsync(legacyBusyPath);}catch(InvalidDataException){legacyBusyBlocked=true;}
Check(legacyBusyBlocked,"legacy_queue_with_durable_rows_never_auto_reset");
Check(File.Exists(legacyBusyPath),"legacy_queue_with_durable_rows_preserved");
Check(Directory.GetFiles(legacyBusyDir,"queue.db.legacy-empty-*.bak").Length==0,"legacy_queue_with_durable_rows_no_backup_swap");

var dir=Path.Combine(Path.GetTempPath(),"sokna-agent-test-"+Guid.NewGuid().ToString("N"));
var path=Path.Combine(dir,"queue.db");
var protector=new TestLeaseProtector();
var store=new LocalQueueStore(path,protector);
await store.InitializeAsync();
Check(await store.CountOpenAsync()==0,"sqlite_init");
Check(await store.GetMetaAsync("schema_version")=="4","sqlite_schema_v4");

var claimEnvelope=new ClaimRequestEnvelope("request-claim-1","6.2.0",4,["bar","kitchen"],3,"2026-09-09T21:00:00.0000000+00:00");
var claimEnvelopeJson=JsonSerializer.Serialize(claimEnvelope,AgentOptions.JsonOptions());
await store.SetMetaAsync("pending_claim_v1",claimEnvelopeJson);
var persistedClaim=JsonSerializer.Deserialize<ClaimRequestEnvelope>(await store.GetMetaAsync("pending_claim_v1")??"",AgentOptions.JsonOptions());
Check(persistedClaim is not null&&persistedClaim.RequestId==claimEnvelope.RequestId&&persistedClaim.AgentVersion==claimEnvelope.AgentVersion&&persistedClaim.ProtocolVersion==claimEnvelope.ProtocolVersion&&persistedClaim.Limit==claimEnvelope.Limit&&persistedClaim.CreatedAt==claimEnvelope.CreatedAt&&persistedClaim.ReadyDestinationKeys.SequenceEqual(claimEnvelope.ReadyDestinationKeys),"claim_replay_complete_envelope_persisted");
var metaRestarted=new LocalQueueStore(path,protector);await metaRestarted.InitializeAsync();
var replayedClaim=JsonSerializer.Deserialize<ClaimRequestEnvelope>(await metaRestarted.GetMetaAsync("pending_claim_v1")??"",AgentOptions.JsonOptions());
Check(replayedClaim?.RequestId==claimEnvelope.RequestId,"claim_replay_request_id_survives_restart");
Check(replayedClaim?.AgentVersion=="6.2.0"&&replayedClaim.ProtocolVersion==4,"claim_replay_version_protocol_survive_restart");
Check(replayedClaim?.Limit==3&&replayedClaim.ReadyDestinationKeys.SequenceEqual(["bar","kitchen"]),"claim_replay_body_survives_restart");
await metaRestarted.DeleteMetaAsync("pending_claim_v1");
Check(await metaRestarted.GetMetaAsync("pending_claim_v1") is null,"claim_replay_meta_explicit_clear");

await store.SetMetaAsync("accept_request:1001","request-accept-stable");
var identityRestarted=new LocalQueueStore(path,protector);await identityRestarted.InitializeAsync();
Check(await identityRestarted.GetMetaAsync("accept_request:1001")=="request-accept-stable","accept_request_identity_survives_restart");

var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"تست\"}";
var sha=CryptoUtil.Sha256Hex(payload);
var destination=new DestinationConfig("prep_shared","آماده‌سازی","Test Queue",80,72.1,1,"combined");
ClaimItem MakeClaim(long attemptId,int attemptNo,string lease="lease-a",string? expiry=null)=>new(
    new ClaimedJob(77,"pub","prep_order",true,"order","77",DateTimeOffset.UtcNow.ToString("O"),4,sha,payload),
    new ClaimAttempt(attemptId,attemptNo,lease,expiry??DateTimeOffset.UtcNow.AddSeconds(45).ToString("O")),
    destination);

var first=MakeClaim(1001,1);
var l1=await store.PersistReservedAsync(first,"receipt-0001","server-a");
Check(l1.AttemptId==1001&&l1.ServerJobId==77,"attempt_1_persist");
Check(l1.ProtectedLeaseToken!="lease-a"&&protector.Unprotect(l1.ProtectedLeaseToken)=="lease-a","lease_not_plaintext_at_local_boundary");
var l1Replay=await store.PersistReservedAsync(first,"receipt-should-not-replace","server-a");
Check(l1Replay.AttemptId==1001&&l1Replay.LocalReceiptId=="receipt-0001","duplicate_claim_same_attempt_idempotent");
Check(await store.CountOpenAsync()==1,"duplicate_claim_does_not_duplicate_open_job");
var alteredPayload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"DIFFERENT\"}";
var altered=first with{Job=first.Job with{PayloadJson=alteredPayload,ContentSha256=CryptoUtil.Sha256Hex(alteredPayload)}};
await ExpectThrowsAsync(()=>store.PersistReservedAsync(altered,"receipt-conflict","server-a"),"duplicate_attempt_different_payload_rejected");
var alteredDestination=first with{Destination=destination with{DestinationKey="other"}};
await ExpectThrowsAsync(()=>store.PersistReservedAsync(alteredDestination,"receipt-conflict","server-a"),"duplicate_attempt_different_destination_rejected");
var badHash=first with{Job=first.Job with{ContentSha256=new string('0',64)}};
await ExpectThrowsAsync(()=>store.PersistReservedAsync(badHash,"receipt-bad-hash","server-a"),"payload_hash_tamper_rejected");
var noOffset=MakeClaim(1099,99,expiry:"2026-09-10T07:00:00");
await ExpectThrowsAsync(()=>store.PersistReservedAsync(noOffset,"receipt-no-offset","server-a"),"lease_timestamp_without_explicit_offset_rejected");

await store.SetStateAsync(1001,LocalJobState.Resolved);
var second=MakeClaim(1002,2,"lease-b");
var l2=await store.PersistReservedAsync(second,"receipt-0002","server-a");
Check(l2.AttemptId==1002&&l2.ServerJobId==77,"same_job_new_attempt_persist");
Check((await store.GetByAttemptAsync(1001)) is not null&&(await store.GetByAttemptAsync(1002)) is not null,"attempt_history_preserved");
Check(await store.CountOpenAsync()==1,"only_new_attempt_open");

var unknownDraft=new AttemptOutcomeDraft(PrintOutcomeStatus.Unknown,null,false,"ambiguous_after_fence","ambiguous after fence","unit-test");
var unknownRequest=new ReportRequestEnvelope("request-report-1002",AgentVersionInfo.Current,4,1002,"receipt-0002","unknown",null,false,"ambiguous_after_fence","ambiguous after fence");
await store.CommitOutcomeAndReportAsync(l2,unknownDraft,unknownRequest);
Check(await store.HasPendingReportAsync(1002),"report_outbox_created");
Check(await store.CountAmbiguousAsync()==1,"ambiguous_outcome_counted_after_durable_commit");
var restarted=new LocalQueueStore(path,protector);await restarted.InitializeAsync();
var recovered=await restarted.GetByAttemptAsync(1002);
var recoveredOutcome=await restarted.GetOutcomeAsync(1002);
var recoveredOutbox=await restarted.GetOutboxForAttemptAsync(1002);
Check(recovered?.State==LocalJobState.Unknown,"unknown_survives_restart");
Check(recoveredOutcome?.Status==PrintOutcomeStatus.Unknown,"unknown_outcome_survives_restart");
Check(recoveredOutbox?.RequestId=="request-report-1002","report_request_id_survives_restart");
await restarted.MarkReportDeliveryAsync(recoveredOutbox!.Id,ReportDeliveryState.Backoff,"network unavailable",503,"server_busy",DateTimeOffset.UtcNow.AddMinutes(1));
Check((await restarted.PendingReportsAsync()).All(x=>x.AttemptId!=1002),"report_transport_failure_gets_backoff_instead_of_hot_loop");
Check(await restarted.HasPendingReportAsync(1002),"backoff_report_remains_durable");

var failedClaim=MakeClaim(1003,3,"lease-c");
var failedJob=await restarted.PersistReservedAsync(failedClaim,"receipt-0003","server-a");
var failedDraft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_open_failed","printer unavailable","worker-result-pre-fence");
var failedRequest=new ReportRequestEnvelope("request-report-1003",AgentVersionInfo.Current,4,1003,"receipt-0003","failed",null,true,"printer_open_failed","printer unavailable");
var failedOutbox=await restarted.CommitOutcomeAndReportAsync(failedJob,failedDraft,failedRequest);
await restarted.MarkReportDeliveryAsync(failedOutbox.Id,ReportDeliveryState.ReconciliationRequired,"terminal conflict",409,"report_conflict",null);
var afterQuarantineRestart=new LocalQueueStore(path,protector);await afterQuarantineRestart.InitializeAsync();
var failedOutcomeAfterRestart=await afterQuarantineRestart.GetOutcomeAsync(1003);
var failedOutboxAfterRestart=await afterQuarantineRestart.GetOutboxForAttemptAsync(1003);
var failedWire=JsonSerializer.Deserialize<ReportRequestEnvelope>(failedOutboxAfterRestart!.BodyJson,AgentOptions.JsonOptions());
Check(failedOutcomeAfterRestart?.Status==PrintOutcomeStatus.Failed,"failed_outcome_survives_quarantine_restart");
Check(failedOutboxAfterRestart.DeliveryState==ReportDeliveryState.ReconciliationRequired,"quarantined_delivery_state_survives_restart");
Check(failedWire?.Status=="failed"&&failedWire.SpoolerJobId is null,"quarantined_failed_never_becomes_submitted");
Check(await afterQuarantineRestart.HasPendingReportAsync(1003),"quarantined_report_still_counts_as_durable_evidence");

var authClaim=MakeClaim(1004,4,"lease-d");
var authJob=await afterQuarantineRestart.PersistReservedAsync(authClaim,"receipt-0004","server-a");
var authDraft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"printer_offline","offline","worker-result-pre-fence");
var authRequest=new ReportRequestEnvelope("request-report-1004",AgentVersionInfo.Current,4,1004,"receipt-0004","failed",null,true,"printer_offline","offline");
await afterQuarantineRestart.CommitOutcomeAndReportAsync(authJob,authDraft,authRequest);
var dispatcher=new ReportDispatcher(afterQuarantineRestart,new ReportDeliveryPolicy(jitter:()=>0.5),new AgentLog(Path.Combine(dir,"logs")));
var transport=new FakePrintTransport{ReportMode=FakeReportMode.Unauthorized};
var authFirst=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
var authBlocked=await afterQuarantineRestart.GetOutboxForAttemptAsync(1004);
Check(authFirst.AuthBlocked==1&&authBlocked?.DeliveryState==ReportDeliveryState.AuthBlocked,"report_401_becomes_auth_blocked");
Check(transport.ReportRequests.Count==1&&transport.ReportRequests[0].RequestId=="request-report-1004","report_401_used_durable_request_identity");
var resumed=await dispatcher.ResumeAfterCredentialProbeAsync("server-a",CancellationToken.None);
Check(resumed>=1,"credential_probe_resumes_auth_blocked_reports");
transport.ReportMode=FakeReportMode.Success;
var authSecond=await dispatcher.DispatchBatchAsync(transport,"server-a",20,CancellationToken.None);
var authDelivered=await afterQuarantineRestart.GetOutboxForAttemptAsync(1004);
Check(authSecond.Delivered>=1&&authDelivered?.DeliveryState==ReportDeliveryState.Delivered,"same_report_delivered_after_token_repair");
var authReplay=transport.ReportRequests.Where(x=>x.AttemptId==1004).ToList();
Check(authReplay.Count==2&&authReplay[0]==authReplay[1],"token_repair_replays_same_report_body_and_request_id");

var policy=new ReportDeliveryPolicy(jitter:()=>0.5);
var forbiddenAuth=policy.ForApiException(new PrintApiException(HttpStatusCode.Forbidden,"revoked","token_revoked"),0,"failed");
var forbiddenPolicy=policy.ForApiException(new PrintApiException(HttpStatusCode.Forbidden,"policy","destination_forbidden"),0,"failed");
var conflictAck=policy.ForApiException(new PrintApiException(HttpStatusCode.Conflict,"already","already_reported","failed"),0,"failed");
var conflictMismatch=policy.ForApiException(new PrintApiException(HttpStatusCode.Conflict,"already","already_reported","submitted"),0,"failed");
var throttled=policy.ForApiException(new PrintApiException((HttpStatusCode)429,"slow","rate_limited",retryAfter:TimeSpan.FromSeconds(30)),0,"failed");
Check(forbiddenAuth.State==ReportDeliveryState.AuthBlocked&&forbiddenAuth.StopCurrentServerScope,"403_revocation_auth_blocked");
Check(forbiddenPolicy.State==ReportDeliveryState.ReconciliationRequired&&!forbiddenPolicy.StopCurrentServerScope,"403_policy_requires_reconciliation");
Check(conflictAck.TreatAsDelivered&&conflictAck.State==ReportDeliveryState.Delivered,"409_idempotent_ack_requires_matching_state");
Check(!conflictMismatch.TreatAsDelivered&&conflictMismatch.State==ReportDeliveryState.ReconciliationRequired,"409_mismatch_not_acknowledged");
Check(throttled.State==ReportDeliveryState.Backoff&&throttled.NextAttemptAt>DateTimeOffset.UtcNow.AddSeconds(20),"429_retry_after_honored");

var scopeClaim=MakeClaim(1005,5,"lease-e");
var scopeJob=await afterQuarantineRestart.PersistReservedAsync(scopeClaim,"receipt-0005","server-old");
var scopeDraft=new AttemptOutcomeDraft(PrintOutcomeStatus.Failed,null,true,"offline","offline","worker-result-pre-fence");
var scopeRequest=new ReportRequestEnvelope("request-report-1005",AgentVersionInfo.Current,4,1005,"receipt-0005","failed",null,true,"offline","offline");
await afterQuarantineRestart.CommitOutcomeAndReportAsync(scopeJob,scopeDraft,scopeRequest);
var beforeScopeCalls=transport.ReportRequests.Count;
var scopeDispatch=await dispatcher.DispatchBatchAsync(transport,"server-new",20,CancellationToken.None);
var scopeOutbox=await afterQuarantineRestart.GetOutboxForAttemptAsync(1005);
Check(scopeDispatch.ReconciliationRequired>=1&&scopeOutbox?.DeliveryState==ReportDeliveryState.ReconciliationRequired,"server_scope_mismatch_blocks_report_delivery");
Check(transport.ReportRequests.Count==beforeScopeCalls,"server_scope_mismatch_does_not_hit_transport");

var durable=Path.Combine(dir,"atomic.txt");
await DurableFile.WriteTextAtomicAsync(durable,"سلام");
Check(File.ReadAllText(durable)=="سلام","durable_atomic_file");
var corruptDir=Path.Combine(Path.GetTempPath(),"sokna-agent-corrupt-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(corruptDir);
var corruptPath=Path.Combine(corruptDir,"queue.db");
await File.WriteAllTextAsync(corruptPath,"not-a-sqlite-database");
await ExpectThrowsAsync(()=>QueueDatabaseBootstrap.PrepareAsync(corruptPath),"queue_bootstrap_corruption_detected");
await ExpectThrowsAsync(()=>new LocalQueueStore(corruptPath,protector).InitializeAsync(),"sqlite_corruption_detected");
try{Directory.Delete(dir,true);}catch{}
try{Directory.Delete(corruptDir,true);}catch{}
try{Directory.Delete(legacyEmptyDir,true);}catch{}
try{Directory.Delete(legacyBusyDir,true);}catch{}
if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}
Console.WriteLine("PASS Sokna.PrintAgent.Tests");
return 0;

static async Task CreateLegacyQueueAsync(string path,bool withDurableRow)
{
    var cs=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString();
    await using var db=new SqliteConnection(cs);
    await db.OpenAsync();
    await using var command=db.CreateCommand();
    command.CommandText="""
    CREATE TABLE local_jobs(server_job_id INTEGER PRIMARY KEY,attempt_id INTEGER,state TEXT);
    CREATE TABLE report_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,attempt_id INTEGER);
    """;
    await command.ExecuteNonQueryAsync();
    if(withDurableRow)
    {
        await using var insert=db.CreateCommand();
        insert.CommandText="INSERT INTO local_jobs(server_job_id,attempt_id,state) VALUES(1,10,'Claimed')";
        await insert.ExecuteNonQueryAsync();
    }
}

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}

enum FakeReportMode{Success,Unauthorized}

sealed class FakePrintTransport:IPrintTransport
{
    public FakeReportMode ReportMode{get;set;}=FakeReportMode.Success;
    public List<ReportRequestEnvelope> ReportRequests{get;}=[];
    public Task<ClaimResponse> ClaimAsync(ClaimRequestEnvelope request,CancellationToken ct)=>Task.FromResult(new ClaimResponse(true,request.RequestId,[],DateTimeOffset.UtcNow.ToString("O"),true));
    public Task<ApiResult> AcceptAsync(ClaimItem item,string localReceiptId,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,AttemptId:item.Attempt.Id,JobId:item.Job.Id,LocalReceiptId:localReceiptId));
    public Task<ApiResult> RenewAsync(ClaimItem item,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,AttemptId:item.Attempt.Id,JobId:item.Job.Id));
    public Task<AttemptStatusResult> AttemptStatusAsync(LocalJob job,CancellationToken ct)=>Task.FromResult(new AttemptStatusResult(true,job.AttemptId,job.ServerJobId,"claimed","claimed",true,"continue",false,false,job.LeaseExpiresAt.ToString("O"),DateTimeOffset.UtcNow.ToString("O")));
    public Task<ApiResult> StartAsync(LocalJob job,string requestId,CancellationToken ct)=>Task.FromResult(new ApiResult(true,Status:"started",AttemptId:job.AttemptId,JobId:job.ServerJobId,CurrentState:"started"));
    public Task<ApiResult> ReportAsync(LocalJob job,ReportRequestEnvelope request,CancellationToken ct)
    {
        ReportRequests.Add(request);
        if(ReportMode==FakeReportMode.Unauthorized)throw new PrintApiException(HttpStatusCode.Unauthorized,"invalid token","invalid_token");
        return Task.FromResult(new ApiResult(true,Status:request.Status,AttemptId:job.AttemptId,JobId:job.ServerJobId,LocalReceiptId:job.LocalReceiptId));
    }
    public Task<ApiResult> HeartbeatAsync(HeartbeatPayload payload,CancellationToken ct)=>Task.FromResult(new ApiResult(true));
    public Task<ProbeResponse> ProbeAsync(CancellationToken ct)=>Task.FromResult(new ProbeResponse(true,4,"6.0.0","6.2.0",[],["attempt_status"],"server-a",DateTimeOffset.UtcNow.ToString("O")));
}
