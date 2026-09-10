using Sokna.PrintAgent.Core;

var failures=new List<string>();
void Check(bool condition,string name){if(!condition)failures.Add(name);}

var root=Path.Combine(Path.GetTempPath(),"sokna-remediation-baseline-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store=new LocalQueueStore(Path.Combine(root,"queue.db"),new TestLeaseProtector());
    await store.InitializeAsync();
    var payload="{\"schema\":\"sokna-print-document-v2\",\"title\":\"baseline\"}";
    var sha=CryptoUtil.Sha256Hex(payload);
    var destination=new DestinationConfig("bar","Bar","Test Queue",80,72,1,"combined");
    var claim=new ClaimItem(
        new ClaimedJob(901,"pub","prep_order",true,"order","901",DateTimeOffset.UtcNow.ToString("O"),4,sha,payload),
        new ClaimAttempt(1901,1,"lease-baseline",DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")),
        destination);
    await store.PersistReservedAsync(claim,"receipt-baseline");

    // Reproduce 6.2 R01: a durable failed report is quarantined, while local state is ReportPending.
    await store.SetStateAsync(1901,LocalJobState.SafeFailed,error:"printer_open_failed");
    await store.EnqueueReportAsync(901,1901,"request-failed-stable","{\"status\":\"failed\",\"retryable\":false,\"error_code\":\"printer_open_failed\"}");
    await store.SetStateAsync(1901,LocalJobState.ReportPending,error:"printer_open_failed");
    var report=(await store.PendingReportsAsync()).Single();
    await store.MarkReportErrorAsync(report.Id,"HTTP 422 contract conflict",permanent:true);

    var restarted=new LocalQueueStore(Path.Combine(root,"queue.db"),new TestLeaseProtector());
    await restarted.InitializeAsync();

    // Expected remediation contract: quarantined delivery remains evidence for the same failed outcome.
    // 6.2 fails this because HasPendingReportAsync excludes permanent_error=1; RecoverAsync may invent submitted.
    Check(await restarted.HasPendingReportAsync(1901),"A01_baseline_failed_quarantined_restart_preserves_report_evidence");

    // Expected remediation contract: auth-blocked delivery can be resumed after credential repair without clearing DB.
    // 6.2 has only permanent_error and no resume transition, therefore there is no dispatchable row after 401/403 quarantine.
    Check((await restarted.PendingReportsAsync()).Any(x=>x.RequestId=="request-failed-stable"),"A06_baseline_401_recovery_without_queue_reset");
}
finally
{
    try{Directory.Delete(root,true);}catch{}
}

if(failures.Count>0)
{
    Console.Error.WriteLine("EXPECTED BASELINE FAILURE: "+string.Join(",",failures));
    return 1;
}
Console.WriteLine("Unexpectedly PASS: baseline defects were not reproduced.");
return 0;

sealed class TestLeaseProtector:ILeaseTokenProtector
{
    public string Protect(string value)=>"test:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
}
