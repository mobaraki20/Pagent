using Sokna.PrintAgent.Core;

var failures=new List<string>();
void Check(bool value,string name){if(!value)failures.Add(name);}
async Task ExpectThrowsAsync(Func<Task> action,string name){try{await action();failures.Add(name);}catch{}}

Check(RecoveryPolicy.Decide(LocalJobState.Reserved,false,false)==RecoveryDecision.ContinueAccept,"restart_before_accept");
Check(RecoveryPolicy.Decide(LocalJobState.Claimed,false,false)==RecoveryDecision.ContinueClaimed,"restart_after_accept");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,false,false)==RecoveryDecision.ContinueClaimed,"crash_before_submission_fence_safe_resume");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,true,false)==RecoveryDecision.RecoveryHold,"crash_after_submission_fence_ambiguous");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,true,true)==RecoveryDecision.Nothing,"durable_worker_result_takes_precedence");
Check(RecoveryPolicy.Decide(LocalJobState.Submitted,true,true)==RecoveryDecision.ReportSubmitted,"crash_after_spooler_before_report_no_reprint");
Check(RecoveryPolicy.Decide(LocalJobState.Unknown,true,false)==RecoveryDecision.RetryReport,"unknown_retry_report_only");
Check(CryptoUtil.Sha256Hex("سلام").Length==64,"sha256");

var dir=Path.Combine(Path.GetTempPath(),"sokna-agent-test-"+Guid.NewGuid().ToString("N"));
var path=Path.Combine(dir,"queue.db");
var protector=new TestLeaseProtector();
var store=new LocalQueueStore(path,protector);
await store.InitializeAsync();
Check(await store.CountOpenAsync()==0,"sqlite_init");

var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"تست\"}";
var sha=CryptoUtil.Sha256Hex(payload);
var destination=new DestinationConfig("prep_shared","آماده‌سازی","Test Queue",80,72.1,1,"combined");
var first=new ClaimItem(
    new ClaimedJob(77,"pub","prep_order",true,"order","77",DateTimeOffset.UtcNow.ToString("O"),4,sha,payload),
    new ClaimAttempt(1001,1,"lease-a",DateTimeOffset.UtcNow.AddSeconds(45).ToString("O")),destination);

var l1=await store.PersistReservedAsync(first,"receipt-0001");
Check(l1.AttemptId==1001&&l1.ServerJobId==77,"attempt_1_persist");
Check(l1.ProtectedLeaseToken!="lease-a"&&protector.Unprotect(l1.ProtectedLeaseToken)=="lease-a","lease_not_plaintext_at_local_boundary");

// Idempotent duplicate claim response must not create a second local row or replace its receipt.
var l1Replay=await store.PersistReservedAsync(first,"receipt-should-not-replace");
Check(l1Replay.AttemptId==1001&&l1Replay.LocalReceiptId=="receipt-0001","duplicate_claim_same_attempt_idempotent");
Check(await store.CountOpenAsync()==1,"duplicate_claim_does_not_duplicate_open_job");

// Same attempt identity with different immutable payload evidence is corruption/conflict, not an overwrite.
var alteredPayload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"DIFFERENT\"}";
var altered=first with{Job=first.Job with{PayloadJson=alteredPayload,ContentSha256=CryptoUtil.Sha256Hex(alteredPayload)}};
await ExpectThrowsAsync(()=>store.PersistReservedAsync(altered,"receipt-conflict"),"duplicate_attempt_different_payload_rejected");
var badHash=first with{Job=first.Job with{ContentSha256=new string('0',64)}};
await ExpectThrowsAsync(()=>store.PersistReservedAsync(badHash,"receipt-bad-hash"),"payload_hash_tamper_rejected");

await store.SetStateAsync(1001,LocalJobState.Resolved);
var second=first with{Attempt=new ClaimAttempt(1002,2,"lease-b",DateTimeOffset.UtcNow.AddSeconds(45).ToString("O"))};
var l2=await store.PersistReservedAsync(second,"receipt-0002");
Check(l2.AttemptId==1002&&l2.ServerJobId==77,"same_job_new_attempt_persist");
Check((await store.GetByAttemptAsync(1001)) is not null&&(await store.GetByAttemptAsync(1002)) is not null,"attempt_history_preserved");
Check(await store.CountOpenAsync()==1,"only_new_attempt_open");

// Outbox is durable evidence for report retry. A restart must retry the report, never re-authorize printing.
await store.SetStateAsync(1002,LocalJobState.Unknown,error:"ambiguous after fence");
await store.EnqueueReportAsync(77,1002,"request-report-1002","{\"status\":\"unknown\"}");
Check(await store.HasPendingReportAsync(1002),"report_outbox_created");
Check(await store.CountAmbiguousAsync()==1,"ambiguous_local_count_before_report");
var restarted=new LocalQueueStore(path,protector);
await restarted.InitializeAsync();
var recovered=await restarted.GetByAttemptAsync(1002);
Check(recovered?.State==LocalJobState.Unknown,"unknown_survives_restart");
var pending=await restarted.PendingReportsAsync();
Check(pending.Count==1&&pending[0].AttemptId==1002&&pending[0].RequestId=="request-report-1002","report_request_id_survives_restart");
await restarted.MarkReportErrorAsync(pending[0].Id,"network unavailable");
Check((await restarted.PendingReportsAsync()).Count==1,"report_network_failure_keeps_outbox");
await restarted.MarkReportSentAsync(pending[0].Id,1002);
Check((await restarted.GetByAttemptAsync(1002))?.State==LocalJobState.Resolved,"report_success_resolves_local_attempt_without_reprint");
Check((await restarted.PendingReportsAsync()).Count==0,"report_outbox_sent_once");

var durable=Path.Combine(dir,"atomic.txt");
await DurableFile.WriteTextAtomicAsync(durable,"سلام");
Check(File.ReadAllText(durable)=="سلام","durable_atomic_file");

// Corrupt SQLite bytes must fail loudly instead of silently replacing durable recovery state.
var corruptDir=Path.Combine(Path.GetTempPath(),"sokna-agent-corrupt-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(corruptDir);
var corruptPath=Path.Combine(corruptDir,"queue.db");
await File.WriteAllTextAsync(corruptPath,"not-a-sqlite-database");
await ExpectThrowsAsync(()=>new LocalQueueStore(corruptPath,protector).InitializeAsync(),"sqlite_corruption_detected");

try{Directory.Delete(dir,true);}catch{}
try{Directory.Delete(corruptDir,true);}catch{}
if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}
Console.WriteLine("PASS Sokna.PrintAgent.Tests");
return 0;

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}
