using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Worker;

if(args.Length==2&&args[0]=="--preview")
{
    try
    {
        var preview=JsonSerializer.Deserialize<PreviewInput>(await File.ReadAllTextAsync(args[1]),AgentOptions.JsonOptions())??throw new InvalidDataException("Preview input معتبر نیست.");
        if(preview.PayloadJson.Length>262144)throw new InvalidDataException("Preview payload بیش از حد مجاز است.");
        if(preview.PaperWidthMm is not (58 or 80)||preview.PrintableWidthMm<20||preview.PrintableWidthMm>preview.PaperWidthMm)throw new InvalidDataException("Preview geometry معتبر نیست.");
        if(preview.DpiX is <100 or >600||preview.DpiY is <100 or >600)throw new InvalidDataException("Preview DPI معتبر نیست.");
        using var bitmap=ReceiptRenderer.Render(preview.PayloadJson,preview.PrintableWidthMm,preview.PaperWidthMm,preview.DpiX,preview.DpiY);
        var directory=Path.GetDirectoryName(preview.OutputPath)??throw new InvalidDataException("Preview output path معتبر نیست.");Directory.CreateDirectory(directory);
        var tmp=preview.OutputPath+"."+Guid.NewGuid().ToString("N")+".tmp";bitmap.Save(tmp,ImageFormat.Png);File.Move(tmp,preview.OutputPath,true);
        var png=await File.ReadAllBytesAsync(preview.OutputPath);var meta=new PreviewResult(true,bitmap.Width,bitmap.Height,preview.DpiX,preview.DpiY,Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),AgentVersionInfo.Current,ReceiptRenderer.ActiveFontFamily,ReceiptRenderer.UsesBundledFont);
        Console.Out.Write(JsonSerializer.Serialize(meta,AgentOptions.JsonOptions()));return 0;
    }
    catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+Safe(e.Message));return 71;}
}

if(args.Length!=1){Console.Error.WriteLine("Usage: Sokna.PrintAgent.Worker <input.json> | --preview <preview.json>");return 64;}
WorkerInput? input=null;
try
{
    input=JsonSerializer.Deserialize<WorkerInput>(await File.ReadAllTextAsync(args[0]),AgentOptions.JsonOptions())??throw new InvalidDataException("Worker input معتبر نیست.");
    if(!string.Equals(CryptoUtil.Sha256Hex(input.PayloadJson),input.ContentSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Hash ورودی Worker نامعتبر است.");
    var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
    while(!File.Exists(input.StartSignalPath))
    {
        if(DateTimeOffset.UtcNow>=deadline)throw new TimeoutException("Start signal از Service دریافت نشد؛ هیچ تماس Spooler انجام نشد.");
        await Task.Delay(50);
    }
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

sealed record PreviewInput(string PayloadJson,double PaperWidthMm,double PrintableWidthMm,int DpiX,int DpiY,string OutputPath);
sealed record PreviewResult(bool Success,int Width,int Height,int DpiX,int DpiY,string PngSha256,string RendererVersion,string FontFamily,bool BundledFont);
