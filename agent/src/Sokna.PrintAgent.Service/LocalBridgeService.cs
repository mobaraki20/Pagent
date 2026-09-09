using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

public sealed class LocalBridgeService : BackgroundService
{
    private readonly AgentPaths _paths;
    private readonly PrintWakeSignal _wake;
    private HttpListener? _listener;
    public LocalBridgeService(AgentPaths paths,PrintWakeSignal wake){_paths=paths;_wake=wake;}
    public static string PairingPath(AgentPaths paths)=>Path.Combine(paths.ProgramDataRoot,"bridge-pairing.id");
    public static string GetOrCreatePairingId(AgentPaths paths)
    {
        var path=PairingPath(paths);if(File.Exists(path)){var value=File.ReadAllText(path,Encoding.UTF8).Trim();if(value.Length>=20)return value;}
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(tmp,token,Encoding.UTF8);File.Move(tmp,path,true);return token;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            AgentOptions options;
            try
            {
                if(!File.Exists(_paths.ConfigPath)){await Task.Delay(1500,stoppingToken);continue;}
                options=AgentOptions.Load(_paths.ConfigPath);options.Validate();
                if(!options.LocalBridgeEnabled){await Task.Delay(3000,stoppingToken);continue;}
                var origin=AllowedOrigin(options);if(origin is null){await Task.Delay(3000,stoppingToken);continue;}
                var pairing=GetOrCreatePairingId(_paths);
                using var listener=new HttpListener();_listener=listener;listener.Prefixes.Add($"http://127.0.0.1:{options.LocalBridgePort}/");listener.Start();
                while(listener.IsListening&&!stoppingToken.IsCancellationRequested)
                {
                    var context=await listener.GetContextAsync().WaitAsync(stoppingToken);
                    _=Task.Run(()=>HandleAsync(context,origin,pairing,stoppingToken),CancellationToken.None);
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception){try{_listener?.Close();}catch{} await Task.Delay(3000,stoppingToken);}
        }
    }
    private static string? AllowedOrigin(AgentOptions options)
    {
        var raw=string.IsNullOrWhiteSpace(options.LocalBridgeAllowedOrigin)?options.ServerBaseUrl:options.LocalBridgeAllowedOrigin;
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri))return null;
        var builder=new UriBuilder(uri.Scheme,uri.Host,uri.IsDefaultPort?-1:uri.Port);return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
    private async Task HandleAsync(HttpListenerContext ctx,string allowedOrigin,string pairing,CancellationToken ct)
    {
        try
        {
            var origin=ctx.Request.Headers["Origin"]??"";
            if(!string.Equals(origin,allowedOrigin,StringComparison.OrdinalIgnoreCase)){ctx.Response.StatusCode=403;return;}
            ctx.Response.Headers["Access-Control-Allow-Origin"]=allowedOrigin;ctx.Response.Headers["Vary"]="Origin";ctx.Response.Headers["Access-Control-Allow-Headers"]="Content-Type, X-Sokna-Bridge-Pairing";ctx.Response.Headers["Access-Control-Allow-Methods"]="POST, OPTIONS";ctx.Response.Headers["Access-Control-Max-Age"]="300";
            if(ctx.Request.HttpMethod=="OPTIONS"){ctx.Response.StatusCode=204;return;}
            if(ctx.Request.HttpMethod!="POST"||ctx.Request.Url?.AbsolutePath!="/v1/wake"){ctx.Response.StatusCode=404;return;}
            var presented=ctx.Request.Headers["X-Sokna-Bridge-Pairing"]??"";if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented),Encoding.UTF8.GetBytes(pairing))){ctx.Response.StatusCode=403;return;}
            if(ctx.Request.ContentLength64<0||ctx.Request.ContentLength64>8192){ctx.Response.StatusCode=413;return;}
            using var reader=new StreamReader(ctx.Request.InputStream,Encoding.UTF8,false,4096,true);var raw=await reader.ReadToEndAsync(ct);if(Encoding.UTF8.GetByteCount(raw)>8192){ctx.Response.StatusCode=413;return;}
            using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("type",out var type)||type.GetString()!="print.wake"||!root.TryGetProperty("protocol_version",out var version)||version.GetInt32()!=1){ctx.Response.StatusCode=422;return;}
            var requestId=root.TryGetProperty("request_id",out var request)?request.GetString()??"":"";if(requestId.Length is <8 or >96){ctx.Response.StatusCode=422;return;}
            if(!root.TryGetProperty("job_ids",out var jobs)||jobs.ValueKind!=JsonValueKind.Array||jobs.GetArrayLength() is <1 or >50){ctx.Response.StatusCode=422;return;}
            foreach(var job in jobs.EnumerateArray())if(job.ValueKind!=JsonValueKind.Number||!job.TryGetInt64(out var id)||id<1){ctx.Response.StatusCode=422;return;}
            if(!root.TryGetProperty("expires_at",out var expires)||expires.ValueKind!=JsonValueKind.String||!DateTimeOffset.TryParse(expires.GetString(),out var expiry)||expiry<DateTimeOffset.UtcNow.AddSeconds(-5)||expiry>DateTimeOffset.UtcNow.AddMinutes(2)){ctx.Response.StatusCode=422;return;}
            _wake.Pulse();ctx.Response.ContentType="application/json; charset=utf-8";var body=Encoding.UTF8.GetBytes("{\"success\":true,\"accepted\":true}");ctx.Response.StatusCode=200;ctx.Response.ContentLength64=body.Length;await ctx.Response.OutputStream.WriteAsync(body,ct);
        }
        catch(JsonException){ctx.Response.StatusCode=400;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch{try{ctx.Response.StatusCode=500;}catch{}}
        finally{try{ctx.Response.Close();}catch{}}
    }
    public override void Dispose(){try{_listener?.Close();}catch{} base.Dispose();}
}
