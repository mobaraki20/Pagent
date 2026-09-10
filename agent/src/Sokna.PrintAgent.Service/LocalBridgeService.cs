using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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
    private static readonly TimeSpan BodyReadTimeout=TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GenerationDrainTimeout=TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreviewTimeout=TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PreviewExitProofTimeout=TimeSpan.FromSeconds(2);

    private readonly AgentPaths _paths;
    private readonly PrintWakeSignal _wake;
    private readonly BridgeRuntimeState _runtime;
    private readonly ConcurrentDictionary<string,DateTimeOffset> _replay=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,(long Revision,DateTimeOffset Seen)> _previewRevisions=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connections=new(MaxConnections,MaxConnections);
    private readonly SemaphoreSlim _previewSlot=new(1,1);
    private HttpListener? _listener;

    public LocalBridgeService(AgentPaths paths,PrintWakeSignal wake,BridgeRuntimeState? runtime=null)
    {
        _paths=paths;
        _wake=wake;
        _runtime=runtime??new BridgeRuntimeState();
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
            catch(HttpListenerException)
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
        var configStamp=FileStamp(_paths.ConfigPath);
        var pairingPath=PairingPath(_paths);
        var pairingStamp=FileStamp(pairingPath);
        using var generationCancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var handlers=new List<Task>(MaxConnections);
        using var listener=new HttpListener();
        _listener=listener;
        listener.IgnoreWriteExceptions=true;
        listener.Prefixes.Add($"http://127.0.0.1:{options.LocalBridgePort}/");
        // HTTP.sys owns the actual socket on Windows. Do not rely only on managed StreamReader
        // cancellation for slow/incomplete request bodies; enforce the same bound in the kernel path.
        listener.TimeoutManager.EntityBody=BodyReadTimeout;
        listener.TimeoutManager.DrainEntityBody=TimeSpan.FromSeconds(1);
        listener.Start();
        _runtime.MarkListening(options.LocalBridgePort,origin,pairing);

        Task<HttpListenerContext>? acceptTask=listener.GetContextAsync();
        try
        {
            while(listener.IsListening&&!ct.IsCancellationRequested)
            {
                var tick=Task.Delay(ReloadProbeInterval,ct);
                var completed=await Task.WhenAny(acceptTask,tick);
                if(completed==tick)
                {
                    ct.ThrowIfCancellationRequested();
                    if(FileStamp(_paths.ConfigPath)!=configStamp||FileStamp(pairingPath)!=pairingStamp)break;
                    continue;
                }

                HttpListenerContext context;
                try{context=await acceptTask;}
                catch(HttpListenerException) when(!ct.IsCancellationRequested){break;}
                acceptTask=listener.GetContextAsync();

                if(!_connections.Wait(0))
                {
                    Reject(context,503,"bridge_busy");
                    continue;
                }
                handlers.RemoveAll(t=>t.IsCompleted);
                handlers.Add(HandleTrackedAsync(context,origin,pairing,generationCancellation.Token));
            }
        }
        finally
        {
            // A config/pairing rotation creates a security boundary: old in-flight handlers must not
            // continue indefinitely with the previous credential/origin after the listener generation ends.
            generationCancellation.Cancel();
            try{listener.Stop();}catch{}
            try{listener.Close();}catch{}
            if(acceptTask is not null)
            {
                try{await acceptTask;}catch{}
            }
            var pending=handlers.Where(t=>!t.IsCompleted).ToArray();
            if(pending.Length>0)
            {
                try{await Task.WhenAll(pending).WaitAsync(GenerationDrainTimeout);}catch{}
            }
            _runtime.MarkStopped(true,null);
            if(ReferenceEquals(_listener,listener))_listener=null;
        }
    }

    private async Task HandleTrackedAsync(HttpListenerContext ctx,string allowedOrigin,string pairing,CancellationToken serviceToken)
    {
        try{await HandleAsync(ctx,allowedOrigin,pairing,serviceToken);}
        finally{_connections.Release();}
    }

    private static string? AllowedOrigin(AgentOptions options)
    {
        var raw=string.IsNullOrWhiteSpace(options.LocalBridgeAllowedOrigin)?options.ServerBaseUrl:options.LocalBridgeAllowedOrigin;
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri))return null;
        return new UriBuilder(uri.Scheme,uri.Host,uri.IsDefaultPort?-1:uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task HandleAsync(HttpListenerContext ctx,string allowedOrigin,string pairing,CancellationToken serviceToken)
    {
        try
        {
            var origin=ctx.Request.Headers["Origin"]??"";
            if(!string.Equals(origin,allowedOrigin,StringComparison.OrdinalIgnoreCase)){ctx.Response.StatusCode=403;return;}
            ctx.Response.Headers["Access-Control-Allow-Origin"]=allowedOrigin;
            ctx.Response.Headers["Vary"]="Origin";
            ctx.Response.Headers["Access-Control-Allow-Headers"]="Content-Type, X-Sokna-Bridge-Pairing";
            ctx.Response.Headers["Access-Control-Allow-Methods"]="POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Max-Age"]="300";

            if(ctx.Request.HttpMethod=="OPTIONS"){ctx.Response.StatusCode=204;return;}
            if(ctx.Request.HttpMethod!="POST"){ctx.Response.StatusCode=405;return;}

            var presented=ctx.Request.Headers["X-Sokna-Bridge-Pairing"]??"";
            var left=Encoding.UTF8.GetBytes(presented);
            var right=Encoding.UTF8.GetBytes(pairing);
            if(left.Length!=right.Length||!CryptographicOperations.FixedTimeEquals(left,right)){ctx.Response.StatusCode=403;return;}

            var mediaType=(ctx.Request.ContentType??"").Split(';',2)[0].Trim();
            if(!string.Equals(mediaType,"application/json",StringComparison.OrdinalIgnoreCase)){ctx.Response.StatusCode=415;return;}

            var path=ctx.Request.Url?.AbsolutePath??"";
            var max=path=="/v1/preview"?262144:8192;
            if(ctx.Request.ContentLength64<0){ctx.Response.StatusCode=411;ctx.Response.KeepAlive=false;return;}
            if(ctx.Request.ContentLength64>max){ctx.Response.StatusCode=413;ctx.Response.KeepAlive=false;return;}

            string raw;
            using(var bodyTimeout=CancellationTokenSource.CreateLinkedTokenSource(serviceToken))
            {
                bodyTimeout.CancelAfter(BodyReadTimeout);
                try
                {
                    using var reader=new StreamReader(ctx.Request.InputStream,Encoding.UTF8,false,8192,true);
                    raw=await reader.ReadToEndAsync(bodyTimeout.Token);
                }
                catch(OperationCanceledException) when(!serviceToken.IsCancellationRequested)
                {
                    ctx.Response.StatusCode=408;
                    ctx.Response.KeepAlive=false;
                    return;
                }
                catch(HttpListenerException) when(!serviceToken.IsCancellationRequested)
                {
                    // HTTP.sys EntityBody timeout may terminate the incomplete request before a 408 body can
                    // be written. The important invariant is bounded resource ownership, not a synthetic ACK.
                    return;
                }
                catch(IOException) when(!serviceToken.IsCancellationRequested)
                {
                    return;
                }
            }
            if(Encoding.UTF8.GetByteCount(raw)>max){ctx.Response.StatusCode=413;ctx.Response.KeepAlive=false;return;}

            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object){ctx.Response.StatusCode=422;return;}
            if(path=="/v1/wake"){await HandleWakeAsync(ctx,root,serviceToken);return;}
            if(path=="/v1/preview"){await HandlePreviewAsync(ctx,root,serviceToken);return;}
            ctx.Response.StatusCode=404;
        }
        catch(JsonException){ctx.Response.StatusCode=400;}
        catch(InvalidOperationException){ctx.Response.StatusCode=422;}
        catch(OperationCanceledException) when(serviceToken.IsCancellationRequested){}
        catch{try{ctx.Response.StatusCode=500;}catch{}}
        finally{try{ctx.Response.Close();}catch{}}
    }

    private async Task HandleWakeAsync(HttpListenerContext ctx,JsonElement root,CancellationToken ct)
    {
        if(!TryString(root,"type",out var type)||type!="print.wake"||!TryInt32(root,"protocol_version",out var version)||version!=1){ctx.Response.StatusCode=422;return;}
        if(!TryString(root,"request_id",out var requestId)||requestId.Length is <8 or >96){ctx.Response.StatusCode=422;return;}
        if(!root.TryGetProperty("job_ids",out var jobs)||jobs.ValueKind!=JsonValueKind.Array||jobs.GetArrayLength() is <1 or >50){ctx.Response.StatusCode=422;return;}
        foreach(var job in jobs.EnumerateArray())if(job.ValueKind!=JsonValueKind.Number||!job.TryGetInt64(out var id)||id<1){ctx.Response.StatusCode=422;return;}
        if(!TryString(root,"expires_at",out var expires)||!HasExplicitOffset(expires)||!DateTimeOffset.TryParse(expires,out var expiry)||expiry<DateTimeOffset.UtcNow.AddSeconds(-5)||expiry>DateTimeOffset.UtcNow.AddMinutes(2)){ctx.Response.StatusCode=422;return;}
        PruneReplay();
        if(!_replay.TryAdd(requestId,expiry)){await WriteJsonAsync(ctx,new{success=true,accepted=true,idempotent=true},ct);return;}
        _wake.Pulse();
        await WriteJsonAsync(ctx,new{success=true,accepted=true,idempotent=false},ct);
    }

    private async Task HandlePreviewAsync(HttpListenerContext ctx,JsonElement root,CancellationToken ct)
    {
        if(!TryString(root,"type",out var type)||type!="print.preview"||!TryInt32(root,"protocol_version",out var version)||version!=1){ctx.Response.StatusCode=422;return;}
        if(!TryInt64(root,"revision",out var revision)||revision<1){ctx.Response.StatusCode=422;return;}
        var sessionId=TryString(root,"session_id",out var session)&&session.Length is >=8 and <=96?session:"legacy-session";
        if(!TryString(root,"payload_json",out var payload)||payload.Length<2||Encoding.UTF8.GetByteCount(payload)>240000){ctx.Response.StatusCode=422;return;}
        if(!TryDouble(root,"paper_width_mm",out var paper)||paper is not (58 or 80)){ctx.Response.StatusCode=422;return;}
        if(!TryDouble(root,"printable_width_mm",out var printable)||printable<20||printable>paper){ctx.Response.StatusCode=422;return;}
        var dpi=TryInt32(root,"dpi",out var requestedDpi)?requestedDpi:203;
        if(dpi is <100 or >600){ctx.Response.StatusCode=422;return;}

        PrunePreviewRevisions();
        var latest=_previewRevisions.AddOrUpdate(sessionId,(revision,DateTimeOffset.UtcNow),(_,old)=>revision>old.Revision?(revision,DateTimeOffset.UtcNow):old);
        if(latest.Revision!=revision)
        {
            await WriteJsonAsync(ctx,new{success=false,code="preview_superseded",revision},ct,409);
            return;
        }
        if(!_previewSlot.Wait(0))
        {
            await WriteJsonAsync(ctx,new{success=false,code="preview_busy",revision},ct,429);
            return;
        }

        var id=Guid.NewGuid().ToString("N");
        var inputPath=Path.Combine(_paths.WorkPath,$"preview-{id}.json");
        var outputPath=Path.Combine(_paths.WorkPath,$"preview-{id}.png");
        var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));
        var preview=new{payload_json=payload,paper_width_mm=paper,printable_width_mm=printable,dpi_x=dpi,dpi_y=dpi,output_path=outputPath};
        await DurableFile.WriteJsonAtomicAsync(inputPath,preview,ct);
        try
        {
            using var process=Process.Start(new ProcessStartInfo(worker,$"--preview \"{inputPath}\"")
            {
                UseShellExecute=false,
                CreateNoWindow=true,
                WorkingDirectory=Path.GetDirectoryName(worker)!,
                RedirectStandardOutput=true,
                RedirectStandardError=true
            })??throw new InvalidOperationException("Preview worker اجرا نشد.");
            using var guard=WorkerProcessGuard.Attach(process);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PreviewTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch(OperationCanceledException)
            {
                await StopPreviewProcessAsync(process);
                if(ct.IsCancellationRequested)throw;
                await WriteJsonAsync(ctx,new{success=false,code="preview_timeout",revision},CancellationToken.None,504);
                return;
            }

            var stdout=await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr=await process.StandardError.ReadToEndAsync(CancellationToken.None);
            if(process.ExitCode!=0||!File.Exists(outputPath))throw new InvalidOperationException("Preview renderer failed: "+Safe(stderr));
            if(_previewRevisions.TryGetValue(sessionId,out var current)&&current.Revision!=revision)
            {
                await WriteJsonAsync(ctx,new{success=false,code="preview_superseded",revision},ct,409);
                return;
            }
            var bytes=await File.ReadAllBytesAsync(outputPath,ct);
            if(bytes.Length>2_000_000)throw new InvalidDataException("Preview image بیش از حد مجاز است.");
            using var meta=JsonDocument.Parse(stdout);
            var m=meta.RootElement;
            await WriteJsonAsync(ctx,new
            {
                success=true,
                revision,
                session_id=sessionId,
                image_base64=Convert.ToBase64String(bytes),
                width=m.GetProperty("width").GetInt32(),
                height=m.GetProperty("height").GetInt32(),
                dpi=m.GetProperty("dpi_x").GetInt32(),
                png_sha256=m.GetProperty("png_sha256").GetString(),
                renderer_version=m.GetProperty("renderer_version").GetString(),
                font_family=m.GetProperty("font_family").GetString(),
                bundled_font=m.GetProperty("bundled_font").GetBoolean()
            },ct);
        }
        finally
        {
            _previewSlot.Release();
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static async Task StopPreviewProcessAsync(Process process)
    {
        try{if(!process.HasExited)process.Kill(true);}catch{}
        if(process.HasExited)return;
        using var proof=new CancellationTokenSource(PreviewExitProofTimeout);
        try{await process.WaitForExitAsync(proof.Token);}catch{}
    }

    private void PruneReplay()
    {
        var now=DateTimeOffset.UtcNow;
        foreach(var row in _replay)if(row.Value<now)_replay.TryRemove(row.Key,out _);
        if(_replay.Count>2048)foreach(var key in _replay.OrderBy(k=>k.Value).Take(_replay.Count-1024).Select(k=>k.Key))_replay.TryRemove(key,out _);
    }

    private void PrunePreviewRevisions()
    {
        var cutoff=DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach(var row in _previewRevisions)if(row.Value.Seen<cutoff)_previewRevisions.TryRemove(row.Key,out _);
        if(_previewRevisions.Count>2048)
            foreach(var key in _previewRevisions.OrderBy(k=>k.Value.Seen).Take(_previewRevisions.Count-1024).Select(k=>k.Key))_previewRevisions.TryRemove(key,out _);
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

    private static async Task WriteJsonAsync(HttpListenerContext ctx,object value,CancellationToken ct,int status=200)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,AgentOptions.JsonOptions());
        ctx.Response.StatusCode=status;
        ctx.Response.ContentType="application/json; charset=utf-8";
        ctx.Response.ContentLength64=bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes,ct);
    }

    private static void Reject(HttpListenerContext ctx,int status,string code)
    {
        try
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new{success=false,code},AgentOptions.JsonOptions());
            ctx.Response.StatusCode=status;
            ctx.Response.KeepAlive=false;
            ctx.Response.ContentType="application/json; charset=utf-8";
            ctx.Response.ContentLength64=bytes.Length;
            ctx.Response.OutputStream.Write(bytes,0,bytes.Length);
        }
        catch{}
        finally{try{ctx.Response.Close();}catch{}}
    }

    private static async Task DelayAfterFailureAsync(CancellationToken ct)
    {
        try{await Task.Delay(TimeSpan.FromSeconds(1),ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){}
    }

    private static string Safe(string s)=>SafeLogText.Sanitize(s,300);
    private static void TryDelete(string p){try{if(File.Exists(p))File.Delete(p);}catch{}}

    public override void Dispose()
    {
        try{_listener?.Close();}catch{}
        _connections.Dispose();
        _previewSlot.Dispose();
        base.Dispose();
    }
}
