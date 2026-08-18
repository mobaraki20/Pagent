using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Worker;

if(args.Length!=1){Console.Error.WriteLine("Usage: Sokna.PrintAgent.Worker <input.json>");return 64;}
WorkerInput? input=null;
try
{
    input=JsonSerializer.Deserialize<WorkerInput>(await File.ReadAllTextAsync(args[0]),AgentOptions.JsonOptions())??throw new InvalidDataException("Worker input معتبر نیست.");
    if(!string.Equals(CryptoUtil.Sha256Hex(input.PayloadJson),input.ContentSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Hash ورودی Worker نامعتبر است.");
    // The worker may not approach the adapter until the Service has attached it to a kill-on-close
    // Windows Job Object and has written this one-shot start signal.
    var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
    while(!File.Exists(input.StartSignalPath))
    {
        if(DateTimeOffset.UtcNow>=deadline)throw new TimeoutException("Start signal از Service دریافت نشد؛ هیچ تماس Spooler انجام نشد.");
        await Task.Delay(50);
    }
    // WinspoolAdapter owns the durable Submission Fence and writes it only after rendering/geometry
    // validation, immediately before StartDoc. This keeps pre-submission failures safely retryable.
    var result=await new WinspoolAdapter().SubmitAsync(input,CancellationToken.None);
    await DurableFile.WriteJsonAtomicAsync(input.ResultPath,result);
    return result.Status=="submitted"?0:result.Status=="failed"?10:20;
}
catch(Exception e)
{
    if(input is not null)
    {
        var status=File.Exists(input.FencePath)?"recovery_hold":"failed";
        var result=new WorkerResult(input.ServerJobId,input.AttemptId,input.LocalReceiptId,input.ContentSha256,status,null,status=="failed","worker_exception",Safe(e.Message));
        try{await DurableFile.WriteJsonAtomicAsync(input.ResultPath,result);}catch{}
    }
    Console.Error.WriteLine(e.GetType().Name+": "+Safe(e.Message));return 70;
}
static string Safe(string s)=>s.Length>400?s[..400]:s;
