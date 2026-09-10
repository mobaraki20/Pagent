using System.Text.Json;
using Sokna.PrintAgent.Core;

if(args.Length!=1)
{
    Console.Error.WriteLine("Expected WorkerInput JSON path.");
    return 64;
}

var inputPath=Path.GetFullPath(args[0]);
var input=JsonSerializer.Deserialize<WorkerInput>(await File.ReadAllTextAsync(inputPath),AgentOptions.JsonOptions())
    ?? throw new InvalidDataException("WorkerInput is invalid.");
var mode=Environment.GetEnvironmentVariable("SOKNA_TEST_WORKER_MODE")??"hang_before_fence";
var pidPath=Environment.GetEnvironmentVariable("SOKNA_TEST_WORKER_PID_PATH");
if(!string.IsNullOrWhiteSpace(pidPath))
    await DurableFile.WriteTextAtomicAsync(pidPath,Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

var startDeadline=DateTimeOffset.UtcNow.AddSeconds(10);
while(!File.Exists(input.StartSignalPath))
{
    if(DateTimeOffset.UtcNow>=startDeadline)throw new TimeoutException("Start signal was not observed by subprocess fixture.");
    await Task.Delay(10);
}

switch(mode)
{
    case "hang_before_fence":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;

    case "fence_then_hang":
        await DurableFile.TouchAtomicAsync(input.FencePath,$"fixture-fence:{input.AttemptId}");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;

    case "submitted_result_then_crash":
        await DurableFile.TouchAtomicAsync(input.FencePath,$"fixture-fence:{input.AttemptId}");
        var result=new WorkerResult(
            input.ServerJobId,
            input.AttemptId,
            input.LocalReceiptId,
            input.ContentSha256,
            "submitted",
            $"fixture-spool-{Environment.ProcessId}",
            false,
            null,
            null);
        await DurableFile.WriteJsonAtomicAsync(input.ResultPath,result);
        return 17;

    default:
        throw new InvalidDataException($"Unsupported subprocess fixture mode: {mode}");
}
