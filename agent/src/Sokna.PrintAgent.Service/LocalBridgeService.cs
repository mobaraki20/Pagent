using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed class LocalBridgeService : BackgroundService
{
    private const int MaxConnections=24;
    private static readonly TimeSpan ReloadProbeInterval=TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HeaderReadTimeout=TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BodyReadTimeout=TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GenerationDrainTimeout=TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding StrictUtf8=new(false,true);

    private readonly AgentPaths _paths;
    private readonly PrintWakeSignal _wake;
    private readonly BridgeRuntimeState _runtime;
    private readonly PreviewScheduler _previewScheduler;
    private readonly AgentLog _log;
    private readonly bool _ownsPreviewScheduler;
    private readonly ConcurrentDictionary<string,DateTimeOffset> _replay=new(StringComparer.Ordinal);
    private LoopbackBridgeServer? _listener;

    public LocalBridgeService(
        AgentPaths paths,
        PrintWakeSignal wake,
        BridgeRuntimeState? runtime=null,
        PreviewScheduler? previewScheduler=null,
        AgentLog? log=null)
    {
        _paths=paths;
        _wake=wake;
        _runtime=runtime??new BridgeRuntimeState();
        _log=log??new AgentLog(paths.LogsPath);
        if(previewScheduler is null)
        {
            _previewScheduler=new PreviewScheduler(new SystemPreviewExecutor(paths,new WorkerSupervisor(new SystemWorkerProcessFactory()),_log));
            _ownsPreviewScheduler=true;
        }
        else
        {
            _previewScheduler=previewScheduler;
        }
    }

    public static string PairingPath(AgentPaths paths)=>Path.Combine(paths.ProgramDataRoot,"bridge-pairing.id");

    /// <summary>
    /// Serializes first creation/repair so concurrent callers can never publish two pairing credentials.
    /// Rotation is performed by replacing the file; the listener generation notices the file stamp and reloads.
    /// </summary>
    public static string GetOrCreatePairingId(AgentPaths paths)
    {
        var path=PairingPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for(var attempt=0;attempt<20;attempt++)
        {
            try
            {
                using var fs=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,4096,FileOptions.WriteThrough);
                using var reader=new StreamReader(fs,Encoding.UTF8,false,1024,true);
                fs.Position=0;
                var existing=reader.ReadToEnd().Trim();
                if(IsPairingValue(existing))return existing;

                var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                fs.SetLength(0);
                fs.Position=0;
                var bytes=Encoding.UTF8.GetBytes(token);
                fs.Write(bytes,0,bytes.Length);
                fs.Flush(true);
                return token;
            }
            catch(IOException) when(attempt<19)
            {
                Thread.Sleep(10);
            }
        }
        throw new IOException("Bridge pairing credential could not be created atomically.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(!File.Exists(_paths.ConfigPath))
                {
                    _runtime.MarkStopped(false,"config_missing");
                    await Task.Delay(ReloadProbeInterval,stoppingToken);
                    continue;
                }

                var options=AgentOptions.Load(_paths.ConfigPath);
                options.Validate();
                if(!options.LocalBridgeEnabled)
                {
                    _runtime.MarkDisabled();
                    await Task.Delay(ReloadProbeInterval,stoppingToken);
                    continue;
                }

                var origin=AllowedOrigin(options);
                if(origin is null)
                {
                    _runtime.MarkStopped(true,"origin_invalid");
                    await Task.Delay(ReloadProbeInterval,stoppingToken);
                    continue;
                }

                var pairing=GetOrCreatePairingId(_paths);
                await RunGenerationAsync(options,origin,pairing,stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch(BridgeBindException)
            {
                _runtime.MarkStopped(true,"bind_failed");
                await DelayAfterFailureAsync(stoppingToken);
            }
            catch(Exception)
            {
                _runtime.MarkStopped(true,"bridge_error");
                await DelayAfterFailureAsync(stoppingToken);
            }
        }
        _runtime.MarkStopped(false,null);
    }

    private async Task RunGenerationAsync(AgentOptions options,string origin,string pairing,CancellationToken ct)
    {
        _previewScheduler.Configure(options.PreviewMaxPendingGlobal,TimeSpan.FromSeconds(options.PreviewRevisionTtlSeconds));
        var configStamp=FileStamp(_paths.ConfigPath);
        var pairingPath=PairingPath(_paths);
        var pairingStamp=FileStamp(pairingPath);
        using var generationCancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var listener=new LoopbackBridgeServer(options.LocalBridgePort,MaxConnections,HeaderReadTimeout,BodyReadTimeout);
        _listener=listener;
        listener.Start();
        _runtime.MarkListening(options.LocalBridgePort,origin,pairing);
        var serveTask=listener.RunAsync((request,requestToken)=>HandleAsync(request,origin,pairing,options,requestToken),generationCancellation.Token);

        try
        {
            while(!ct.IsCancellationRequested)
            {
                var tick=Task.Delay(ReloadProbeInterval,ct);
                var completed=await Task.WhenAny(serveTask,tick);
                if(completed==serveTask)
                {
                    await serveTask;
                    if(!ct.IsCancellationRequested)throw new IOException("Loopback bridge listener stopped unexpectedly.");
                    break;
                }

                ct.ThrowIfCancellationRequested();
                if(FileStamp(_paths.ConfigPath)!=configStamp||FileStamp(pairingPath)!=pairingStamp)break;
            }
        }
        finally
        {
            generationCancellation.Cancel();
            listener.Stop();
            try{await serveTask.WaitAsync(GenerationDrainTimeout);}catch{}
            _runtime.MarkStopped(true,null);
            if(ReferenceEquals(_listener,listener))_listener=null;
        }
    }

    private static string? AllowedOrigin(AgentOptions options)
    {
        var raw=string.IsNullOrWhiteSpace(options.LocalBridgeAllowedOrigin)?options.ServerBaseUrl:options.LocalBridgeAllowedOrigin;
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri))return null;
        return new UriBuilder(uri.Scheme,uri.Host,uri.IsDefaultPort?-1:uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task<BridgeHttpResponse> HandleAsync(
        BridgeHttpRequest request,
        string allowedOrigin,
        string pairing,
        AgentOptions options,
        CancellationToken serviceToken)
    {
        var response=new BridgeHttpResponse();
        try
        {
            var origin=request.Header("Origin")??"";
            if(!string.Equals(origin,allowedOrigin,StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode=403;
                return response;
            }
            ApplyCors(response,allowedOrigin);

            if(string.Equals(request.Method,"OPTIONS",StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode=204;
                return response;
            }
            if(!string.Equals(request.Method,"POST",StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode=405;
                return response;
            }

            var presented=request.Header("X-Sokna-Bridge-Pairing")??"";
            var left=Encoding.UTF8.GetBytes(presented);
            var right=Encoding.UTF8.GetBytes(pairing);
            if(left.Length!=right.Length||!CryptographicOperations.FixedTimeEquals(left,right))
            {
                response.StatusCode=403;
                return response;
            }

            var mediaType=(request.Header("Content-Type")??"").Split(';',2)[0].Trim();
            if(!string.Equals(mediaType,"application/json",StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode=415;
                return response;
            }

            string raw;
            try{raw=StrictUtf8.GetString(request.Body);}
            catch(DecoderFallbackException)
            {
                response.StatusCode=400;
                return response;
            }

            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)
            {
                response.StatusCode=422;
                return response;
            }
            if(request.Path=="/v1/wake")
            {
                await HandleWakeAsync(response,root,serviceToken);
                return response;
            }
            if(request.Path=="/v1/preview")
            {
                await HandlePreviewAsync(response,root,options,serviceToken);
                return response;
            }
            response.StatusCode=404;
            return response;
        }
        catch(JsonException)
        {
            response.StatusCode=400;
            response.Body=[];
            response.ContentType=null;
            return response;
        }
        catch(InvalidOperationException)
        {
            response.StatusCode=422;
            response.Body=[];
            response.ContentType=null;
            return response;
        }
        catch(OperationCanceledException) when(serviceToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            response.StatusCode=500;
            response.Body=[];
            response.ContentType=null;
            return response;
        }
    }

    private async Task HandleWakeAsync(BridgeHttpResponse response,JsonElement root,CancellationToken ct)
    {
        if(!TryString(root,"type",out var type)||type!="print.wake"||!TryInt32(root,"protocol_version",out var version)||version!=1){response.StatusCode=422;return;}
        if(!TryString(root,"request_id",out var requestId)||requestId.Length is <8 or >96){response.StatusCode=422;return;}
        if(!root.TryGetProperty("job_ids",out var jobs)||jobs.ValueKind!=JsonValueKind.Array||jobs.GetArrayLength() is <1 or >50){response.StatusCode=422;return;}
        foreach(var job in jobs.EnumerateArray())if(job.ValueKind!=JsonValueKind.Number||!job.TryGetInt64(out var id)||id<1){response.StatusCode=422;return;}
        if(!TryString(root,"expires_at",out var expires)||!HasExplicitOffset(expires)||!DateTimeOffset.TryParse(expires,out var expiry)||expiry<DateTimeOffset.UtcNow.AddSeconds(-5)||expiry>DateTimeOffset.UtcNow.AddMinutes(2)){response.StatusCode=422;return;}
        PruneReplay();
        if(!_replay.TryAdd(requestId,expiry))
        {
            SetJson(response,new{success=true,accepted=true,idempotent=true});
            return;
        }
        _wake.Pulse();
        _log.Info("wake_received",$"request={Short(requestId)}; jobs={jobs.GetArrayLength()}");
        SetJson(response,new{success=true,accepted=true,idempotent=false});
        await Task.CompletedTask;
    }

    private async Task HandlePreviewAsync(BridgeHttpResponse response,JsonElement root,AgentOptions options,CancellationToken ct)
    {
        if(!TryString(root,"type",out var type)||type!="print.preview"||!TryInt32(root,"protocol_version",out var version)||version!=1){response.StatusCode=422;return;}
        if(!TryInt64(root,"revision",out var revision)||revision<1){response.StatusCode=422;return;}
        if(!TryString(root,"session_id",out var sessionId)||sessionId.Length is <8 or >96){response.StatusCode=422;return;}
        if(!TryString(root,"payload_json",out var payload)){response.StatusCode=422;return;}
        if(!TryDouble(root,"paper_width_mm",out var paper)){response.StatusCode=422;return;}
        if(!TryDouble(root,"printable_width_mm",out var printable)){response.StatusCode=422;return;}

        var legacyDpi=TryInt32(root,"dpi",out var dpi)?dpi:203;
        var dpiX=TryInt32(root,"dpi_x",out var requestedDpiX)?requestedDpiX:legacyDpi;
        var dpiY=TryInt32(root,"dpi_y",out var requestedDpiY)?requestedDpiY:legacyDpi;
        var limits=new PreviewSafetyLimits(
            options.PreviewMaxPayloadBytes,
            options.PreviewMaxTextCharacters,
            options.PreviewMaxItems,
            options.PreviewMaxHeightPixels,
            options.PreviewMaxPixelArea,
            options.PreviewMaxOutputBytes);
        try
        {
            _=PreviewSafety.Validate(payload,paper,printable,dpiX,dpiY,limits);
        }
        catch(InvalidDataException)
        {
            SetJson(response,new{success=false,code="preview_limits_invalid",revision,session_id=sessionId},422);
            return;
        }

        var request=new PreviewWorkRequest(
            sessionId,
            revision,
            payload,
            paper,
            printable,
            dpiX,
            dpiY,
            limits,
            TimeSpan.FromSeconds(options.PreviewTimeoutSeconds),
            TimeSpan.FromMilliseconds(options.PreviewExitProofTimeoutMilliseconds));
        var result=await _previewScheduler.SubmitAsync(request,ct);
        if(ct.IsCancellationRequested)throw new OperationCanceledException(ct);

        switch(result.Status)
        {
            case PreviewScheduleStatus.Completed when result.Render is { } render:
                SetJson(response,new
                {
                    success=true,
                    revision,
                    session_id=sessionId,
                    image_base64=Convert.ToBase64String(render.ImageBytes),
                    width=render.Width,
                    height=render.Height,
                    dpi=render.DpiX,
                    dpi_x=render.DpiX,
                    dpi_y=render.DpiY,
                    png_sha256=render.PngSha256,
                    renderer_version=render.RendererVersion,
                    font_family=render.FontFamily,
                    bundled_font=render.BundledFont
                });
                return;
            case PreviewScheduleStatus.Busy:
                response.Headers["Retry-After"]="1";
                SetJson(response,new{success=false,code="preview_busy",revision,session_id=sessionId},429);
                return;
            case PreviewScheduleStatus.Superseded:
                SetJson(response,new{success=false,code="preview_superseded",revision,session_id=sessionId},409);
                return;
            case PreviewScheduleStatus.Timeout:
                SetJson(response,new{success=false,code="preview_timeout",revision,session_id=sessionId},504);
                return;
            case PreviewScheduleStatus.Cancelled:
                SetJson(response,new{success=false,code="preview_cancelled",revision,session_id=sessionId},409);
                return;
            default:
                var status=result.Code is "preview_raster_budget_exceeded" or "preview_output_size_exceeded" or "preview_metadata_mismatch"?422:500;
                SetJson(response,new{success=false,code=result.Code,revision,session_id=sessionId},status);
                return;
        }
    }

    private static void ApplyCors(BridgeHttpResponse response,string allowedOrigin)
    {
        response.Headers["Access-Control-Allow-Origin"]=allowedOrigin;
        response.Headers["Vary"]="Origin";
        response.Headers["Access-Control-Allow-Headers"]="Content-Type, X-Sokna-Bridge-Pairing";
        response.Headers["Access-Control-Allow-Methods"]="POST, OPTIONS";
        response.Headers["Access-Control-Max-Age"]="300";
    }

    private static void SetJson(BridgeHttpResponse response,object value,int status=200)
    {
        response.StatusCode=status;
        response.ContentType="application/json; charset=utf-8";
        response.Body=JsonSerializer.SerializeToUtf8Bytes(value,AgentOptions.JsonOptions());
    }

    private void PruneReplay()
    {
        var now=DateTimeOffset.UtcNow;
        foreach(var row in _replay)if(row.Value<now)_replay.TryRemove(row.Key,out _);
        if(_replay.Count>2048)foreach(var key in _replay.OrderBy(k=>k.Value).Take(_replay.Count-1024).Select(k=>k.Key))_replay.TryRemove(key,out _);
    }

    private static (DateTime LastWriteUtc,long Length,bool Exists) FileStamp(string path)
    {
        try
        {
            var info=new FileInfo(path);
            return info.Exists?(info.LastWriteTimeUtc,info.Length,true):(DateTime.MinValue,0,false);
        }
        catch{return(DateTime.MinValue,0,false);}
    }

    private static bool IsPairingValue(string value)
        =>value.Length is >=20 and <=128&&value.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '-' or '_');

    private static bool TryString(JsonElement root,string name,out string value)
    {
        value="";
        if(!root.TryGetProperty(name,out var element)||element.ValueKind!=JsonValueKind.String)return false;
        value=element.GetString()??"";
        return true;
    }

    private static bool TryInt32(JsonElement root,string name,out int value)
    {
        value=0;
        return root.TryGetProperty(name,out var element)&&element.ValueKind==JsonValueKind.Number&&element.TryGetInt32(out value);
    }

    private static bool TryInt64(JsonElement root,string name,out long value)
    {
        value=0;
        return root.TryGetProperty(name,out var element)&&element.ValueKind==JsonValueKind.Number&&element.TryGetInt64(out value);
    }

    private static bool TryDouble(JsonElement root,string name,out double value)
    {
        value=0;
        return root.TryGetProperty(name,out var element)&&element.ValueKind==JsonValueKind.Number&&element.TryGetDouble(out value)&&double.IsFinite(value);
    }

    private static bool HasExplicitOffset(string value)
    {
        if(string.IsNullOrWhiteSpace(value))return false;
        var text=value.Trim();
        var t=text.IndexOf('T');
        var offset=Math.Max(text.LastIndexOf('+'),text.LastIndexOf('-'));
        return (text.EndsWith('Z')||(offset>t&&offset>=0))&&DateTimeOffset.TryParse(text,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out _);
    }

    private static async Task DelayAfterFailureAsync(CancellationToken ct)
    {
        try{await Task.Delay(TimeSpan.FromSeconds(1),ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){}
    }

    private static string Short(string value)=>value.Length<=12?value:value[..12];

    public override void Dispose()
    {
        try{_listener?.Stop();}catch{}
        if(_ownsPreviewScheduler)
        {
            try{_previewScheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();}catch{}
        }
        base.Dispose();
    }
}
