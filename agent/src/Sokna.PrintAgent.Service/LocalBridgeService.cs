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
    private readonly AgentPaths _paths;private readonly PrintWakeSignal _wake;private HttpListener? _listener;
    private readonly ConcurrentDictionary<string,DateTimeOffset> _replay=new(StringComparer.Ordinal);
    public LocalBridgeService(AgentPaths paths,PrintWakeSignal wake){_paths=paths;_wake=wake;}
    public static string PairingPath(AgentPaths paths)=>Path.Combine(paths.ProgramDataRoot,"bridge-pairing.id");
    public static string GetOrCreatePairingId(AgentPaths paths)
    {
        var path=PairingPath(paths);if(File.Exists(path)){var value=File.ReadAllText(path,Encoding.UTF8).Trim();if(value.Length>=20)return value;}
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(tmp,token,Encoding.UTF8);File.Move(tmp,path,true);return token;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(!File.Exists(_paths.ConfigPath)){await Task.Delay(1500,stoppingToken);continue;}
                var options=AgentOptions.Load(_paths.ConfigPath);options.Validate();if(!options.LocalBridgeEnabled){await Task.Delay(3000,stoppingToken);continue;}
                var origin=AllowedOrigin(options);if(origin is null){await Task.Delay(3000,stoppingToken);continue;}var pairing=GetOrCreatePairingId(_paths);
                using var listener=new HttpListener();_listener=listener;listener.Prefixes.Add($"http://127.0.0.1:{options.LocalBridgePort}/");listener.Start();
                while(listener.IsListening&&!stoppingToken.IsCancellationRequested){var context=await listener.GetContextAsync().WaitAsync(stoppingToken);_ = Task.Run(()=>HandleAsync(context,origin,pairing,stoppingToken),CancellationToken.None);}
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception){try{_listener?.Close();}catch{} await Task.Delay(3000,stoppingToken);}
        }
    }
    private static string? AllowedOrigin(AgentOptions options)
    {
        var raw=string.IsNullOrWhiteSpace(options.LocalBridgeAllowedOrigin)?options.ServerBaseUrl:options.LocalBridgeAllowedOrigin;if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri))return null;return new UriBuilder(uri.Scheme,uri.Host,uri.IsDefaultPort?-1:uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }
    private async Task HandleAsync(HttpListenerContext ctx,string allowedOrigin,string pairing,CancellationToken ct)
    {
        try
        {
            var origin=ctx.Request.Headers["Origin"]??"";if(!string.Equals(origin,allowedOrigin,StringComparison.OrdinalIgnoreCase)){ctx.Response.StatusCode=403;return;}
            ctx.Response.Headers["Access-Control-Allow-Origin"]=allowedOrigin;ctx.Response.Headers["Vary"]="Origin";ctx.Response.Headers["Access-Control-Allow-Headers"]="Content-Type, X-Sokna-Bridge-Pairing";ctx.Response.Headers["Access-Control-Allow-Methods"]="POST, OPTIONS";ctx.Response.Headers["Access-Control-Max-Age"]="300";
            if(ctx.Request.HttpMethod=="OPTIONS"){ctx.Response.StatusCode=204;return;}if(ctx.Request.HttpMethod!="POST"){ctx.Response.StatusCode=405;return;}
            var presented=ctx.Request.Headers["X-Sokna-Bridge-Pairing"]??"";var left=Encoding.UTF8.GetBytes(presented);var right=Encoding.UTF8.GetBytes(pairing);if(left.Length!=right.Length||!CryptographicOperations.FixedTimeEquals(left,right)){ctx.Response.StatusCode=403;return;}
            var path=ctx.Request.Url?.AbsolutePath??"";var max=path=="/v1/preview"?262144:8192;if(ctx.Request.ContentLength64<0||ctx.Request.ContentLength64>max){ctx.Response.StatusCode=413;return;}
            using var reader=new StreamReader(ctx.Request.InputStream,Encoding.UTF8,false,8192,true);var raw=await reader.ReadToEndAsync(ct);if(Encoding.UTF8.GetByteCount(raw)>max){ctx.Response.StatusCode=413;return;}
            using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;if(root.ValueKind!=JsonValueKind.Object){ctx.Response.StatusCode=422;return;}
            if(path=="/v1/wake"){await HandleWakeAsync(ctx,root,ct);return;}if(path=="/v1/preview"){await HandlePreviewAsync(ctx,root,ct);return;}ctx.Response.StatusCode=404;
        }
        catch(JsonException){ctx.Response.StatusCode=400;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch{try{ctx.Response.StatusCode=500;}catch{}}
        finally{try{ctx.Response.Close();}catch{}}
    }
    private async Task HandleWakeAsync(HttpListenerContext ctx,JsonElement root,CancellationToken ct)
    {
        if(!root.TryGetProperty("type",out var type)||type.GetString()!="print.wake"||!root.TryGetProperty("protocol_version",out var version)||version.GetInt32()!=1){ctx.Response.StatusCode=422;return;}
        var requestId=root.TryGetProperty("request_id",out var request)?request.GetString()??"":"";if(requestId.Length is <8 or >96){ctx.Response.StatusCode=422;return;}
        if(!root.TryGetProperty("job_ids",out var jobs)||jobs.ValueKind!=JsonValueKind.Array||jobs.GetArrayLength() is <1 or >50){ctx.Response.StatusCode=422;return;}foreach(var job in jobs.EnumerateArray())if(job.ValueKind!=JsonValueKind.Number||!job.TryGetInt64(out var id)||id<1){ctx.Response.StatusCode=422;return;}
        if(!root.TryGetProperty("expires_at",out var expires)||expires.ValueKind!=JsonValueKind.String||!DateTimeOffset.TryParse(expires.GetString(),out var expiry)||expiry<DateTimeOffset.UtcNow.AddSeconds(-5)||expiry>DateTimeOffset.UtcNow.AddMinutes(2)){ctx.Response.StatusCode=422;return;}
        PruneReplay();if(!_replay.TryAdd(requestId,expiry)){await WriteJsonAsync(ctx,new{success=true,accepted=true,idempotent=true},ct);return;}_wake.Pulse();await WriteJsonAsync(ctx,new{success=true,accepted=true,idempotent=false},ct);
    }
    private async Task HandlePreviewAsync(HttpListenerContext ctx,JsonElement root,CancellationToken ct)
    {
        if(!root.TryGetProperty("type",out var type)||type.GetString()!="print.preview"||!root.TryGetProperty("protocol_version",out var version)||version.GetInt32()!=1){ctx.Response.StatusCode=422;return;}
        var revision=root.TryGetProperty("revision",out var rev)&&rev.TryGetInt64(out var r)?r:0;if(revision<1){ctx.Response.StatusCode=422;return;}
        var payload=root.TryGetProperty("payload_json",out var payloadEl)&&payloadEl.ValueKind==JsonValueKind.String?payloadEl.GetString()??"":"";if(payload.Length<2||Encoding.UTF8.GetByteCount(payload)>240000){ctx.Response.StatusCode=422;return;}
        var paper=root.TryGetProperty("paper_width_mm",out var paperEl)&&paperEl.TryGetDouble(out var p)?p:0;var printable=root.TryGetProperty("printable_width_mm",out var printableEl)&&printableEl.TryGetDouble(out var pw)?pw:0;var dpi=root.TryGetProperty("dpi",out var dpiEl)&&dpiEl.TryGetInt32(out var d)?d:203;
        if(paper is not (58 or 80)||printable<20||printable>paper||dpi is <100 or >600){ctx.Response.StatusCode=422;return;}
        var id=Guid.NewGuid().ToString("N");var inputPath=Path.Combine(_paths.WorkPath,$"preview-{id}.json");var outputPath=Path.Combine(_paths.WorkPath,$"preview-{id}.png");var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));
        var preview=new{payload_json=payload,paper_width_mm=paper,printable_width_mm=printable,dpi_x=dpi,dpi_y=dpi,output_path=outputPath};await DurableFile.WriteJsonAtomicAsync(inputPath,preview,ct);
        try
        {
            using var process=Process.Start(new ProcessStartInfo(worker,$"--preview \"{inputPath}\""){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(worker)!,RedirectStandardOutput=true,RedirectStandardError=true})??throw new InvalidOperationException("Preview worker اجرا نشد.");using var guard=WorkerProcessGuard.Attach(process);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));await process.WaitForExitAsync(timeout.Token);var stdout=await process.StandardOutput.ReadToEndAsync(ct);var stderr=await process.StandardError.ReadToEndAsync(ct);if(process.ExitCode!=0||!File.Exists(outputPath))throw new InvalidOperationException("Preview renderer failed: "+Safe(stderr));var bytes=await File.ReadAllBytesAsync(outputPath,ct);if(bytes.Length>2_000_000)throw new InvalidDataException("Preview image بیش از حد مجاز است.");using var meta=JsonDocument.Parse(stdout);var m=meta.RootElement;await WriteJsonAsync(ctx,new{success=true,revision,image_base64=Convert.ToBase64String(bytes),width=m.GetProperty("width").GetInt32(),height=m.GetProperty("height").GetInt32(),dpi=m.GetProperty("dpi_x").GetInt32(),png_sha256=m.GetProperty("png_sha256").GetString(),renderer_version=m.GetProperty("renderer_version").GetString(),font_family=m.GetProperty("font_family").GetString(),bundled_font=m.GetProperty("bundled_font").GetBoolean()},ct);
        }
        finally{TryDelete(inputPath);TryDelete(outputPath);}
    }
    private void PruneReplay(){var now=DateTimeOffset.UtcNow;foreach(var row in _replay)if(row.Value<now)_replay.TryRemove(row.Key,out _);if(_replay.Count>2048)foreach(var key in _replay.OrderBy(k=>k.Value).Take(_replay.Count-1024).Select(k=>k.Key))_replay.TryRemove(key,out _);}
    private static async Task WriteJsonAsync(HttpListenerContext ctx,object value,CancellationToken ct){var bytes=JsonSerializer.SerializeToUtf8Bytes(value,AgentOptions.JsonOptions());ctx.Response.StatusCode=200;ctx.Response.ContentType="application/json; charset=utf-8";ctx.Response.ContentLength64=bytes.Length;await ctx.Response.OutputStream.WriteAsync(bytes,ct);}
    private static string Safe(string s)=>s.Length>300?s[..300]:s;private static void TryDelete(string p){try{if(File.Exists(p))File.Delete(p);}catch{}}
    public override void Dispose(){try{_listener?.Close();}catch{} base.Dispose();}
}
