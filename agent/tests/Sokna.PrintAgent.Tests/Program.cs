using Sokna.PrintAgent.Core;
var failures=new List<string>();void Check(bool value,string name){if(!value)failures.Add(name);}
Check(RecoveryPolicy.Decide(LocalJobState.Reserved,false,false)==RecoveryDecision.ContinueAccept,"restart_before_accept");
Check(RecoveryPolicy.Decide(LocalJobState.Claimed,false,false)==RecoveryDecision.ContinueClaimed,"restart_after_accept");
Check(RecoveryPolicy.Decide(LocalJobState.WorkerLaunching,true,false)==RecoveryDecision.RecoveryHold,"crash_during_spooler_ambiguous");
Check(RecoveryPolicy.Decide(LocalJobState.Submitted,true,true)==RecoveryDecision.ReportSubmitted,"crash_after_spooler_before_report_no_reprint");
Check(CryptoUtil.Sha256Hex("سلام").Length==64,"sha256");
var dir=Path.Combine(Path.GetTempPath(),"sokna-agent-test-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(dir,"queue.db");var store=new LocalQueueStore(path,new TestLeaseProtector());await store.InitializeAsync();Check(await store.CountOpenAsync()==0,"sqlite_init");
var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"تست\"}";var sha=CryptoUtil.Sha256Hex(payload);var destination=new DestinationConfig("prep_shared","آماده‌سازی","Test Queue",80,72.1,1,"combined");
var first=new ClaimItem(new ClaimedJob(77,"pub","prep_order",true,"order","77",DateTimeOffset.UtcNow.ToString("O"),4,sha,payload),new ClaimAttempt(1001,1,"lease-a",DateTimeOffset.UtcNow.AddSeconds(45).ToString("O")),destination);
var l1=await store.PersistReservedAsync(first,"receipt-0001");Check(l1.AttemptId==1001&&l1.ServerJobId==77,"attempt_1_persist");await store.SetStateAsync(1001,LocalJobState.Resolved);
var second=first with{Attempt=new ClaimAttempt(1002,2,"lease-b",DateTimeOffset.UtcNow.AddSeconds(45).ToString("O"))};var l2=await store.PersistReservedAsync(second,"receipt-0002");Check(l2.AttemptId==1002&&l2.ServerJobId==77,"same_job_new_attempt_persist");Check((await store.GetByAttemptAsync(1001)) is not null&&(await store.GetByAttemptAsync(1002)) is not null,"attempt_history_preserved");Check(await store.CountOpenAsync()==1,"only_new_attempt_open");
var durable=Path.Combine(dir,"atomic.txt");await DurableFile.WriteTextAtomicAsync(durable,"سلام");Check(File.ReadAllText(durable)=="سلام","durable_atomic_file");
try{Directory.Delete(dir,true);}catch{}
if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}Console.WriteLine("PASS Sokna.PrintAgent.Tests");return 0;

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}
