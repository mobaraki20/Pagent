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
        var requestedLimits=new PreviewSafetyLimits(
            preview.MaxPayloadBytes,
            preview.MaxTextCharacters,
            preview.MaxItems,
            preview.MaxHeightPixels,
            preview.MaxPixelArea,
            preview.MaxOutputBytes).ClampToAbsolute();
        var metrics=PreviewSafety.Validate(
            preview.PayloadJson,
            preview.PaperWidthMm,
            preview.PrintableWidthMm,
            preview.DpiX,
            preview.DpiY,
            requestedLimits);

        // ReceiptRenderer currently stages against a bounded logical-height canvas. Prove that
        // allocation fits the preview raster budget before calling Render; this prevents a small
        // JSON request with extreme DPI from allocating an unexpectedly large bitmap.
        var rendererStagingHeight=Math.Clamp((int)Math.Round(16000*preview.DpiY/203d),16000,48000);
        if((long)metrics.WidthPixels*rendererStagingHeight>requestedLimits.MaxPixelArea)
            throw new InvalidDataException("Preview raster allocation از سقف pixel area مجاز عبور می‌کند.");

        using var bitmap=ReceiptRenderer.Render(preview.PayloadJson,preview.PrintableWidthMm,preview.PaperWidthMm,preview.DpiX,preview.DpiY);
        if(bitmap.Height>requestedLimits.MaxHeightPixels||(long)bitmap.Width*bitmap.Height>requestedLimits.MaxPixelArea)
            throw new InvalidDataException("Preview output geometry از سقف مجاز عبور کرده است.");

        var directory=Path.GetDirectoryName(preview.OutputPath)??throw new InvalidDataException("Preview output path معتبر نیست.");
        Directory.CreateDirectory(directory);
        var tmp=preview.OutputPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            bitmap.Save(tmp,ImageFormat.Png);
            var tmpInfo=new FileInfo(tmp);
            if(tmpInfo.Length<8||tmpInfo.Length>requestedLimits.MaxOutputBytes)
                throw new InvalidDataException("Preview PNG از سقف اندازه خروجی مجاز عبور کرده است.");
            File.Move(tmp,preview.OutputPath,true);
        }
        finally
        {
            try{if(File.Exists(tmp))File.Delete(tmp);}catch{}
        }

        var png=await File.ReadAllBytesAsync(preview.OutputPath);
        if(png.Length>requestedLimits.MaxOutputBytes)throw new InvalidDataException("Preview PNG از سقف اندازه خروجی مجاز عبور کرده است.");
        var meta=new PreviewResult(
            true,
            bitmap.Width,
            bitmap.Height,
            preview.DpiX,
            preview.DpiY,
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
            AgentVersionInfo.Current,
            ReceiptRenderer.ActiveFontFamily,
            ReceiptRenderer.UsesBundledFont);
        Console.Out.Write(JsonSerializer.Serialize(meta,AgentOptions.JsonOptions()));
        return 0;
    }
    catch(Exception e)
    {
        Console.Error.WriteLine(e.GetType().Name+": "+Safe(e.Message));
        return 71;
    }
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

    // A stale server destination may still reference the virtual queue after UAT mode was turned off.
    // Fail before the submission fence so production can never silently become a file-only print path.
    if(VirtualPrinterQueues.IsPdfTestQueue(input.QueueName)&&!PdfTestModePolicy.IsEnabled())
    {
        var disabled=new WorkerResult(
            input.ServerJobId,input.AttemptId,input.LocalReceiptId,input.ContentSha256,
            "failed",null,true,"pdf_test_mode_disabled",
            "PDF Test Sink خاموش است؛ مقصد را به پرینتر فیزیکی برگردانید یا Test/UAT Mode را صریحاً فعال کنید.");
        await DurableFile.WriteJsonAtomicAsync(input.ResultPath,disabled);
        return 10;
    }

    IPrinterAdapter adapter=VirtualPrinterQueues.IsPdfTestQueue(input.QueueName)
        ?new PdfTestSinkAdapter()
        :new WinspoolAdapter();
    var result=await adapter.SubmitAsync(input,CancellationToken.None);
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

sealed record PreviewInput(
    string PayloadJson,
    double PaperWidthMm,
    double PrintableWidthMm,
    int DpiX,
    int DpiY,
    string OutputPath,
    int MaxPayloadBytes=240000,
    int MaxTextCharacters=100000,
    int MaxItems=500,
    int MaxHeightPixels=24000,
    long MaxPixelArea=24000000,
    int MaxOutputBytes=2000000);
sealed record PreviewResult(bool Success,int Width,int Height,int DpiX,int DpiY,string PngSha256,string RendererVersion,string FontFamily,bool BundledFont);
