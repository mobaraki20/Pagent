using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sokna.PrintAgent.Service;

internal sealed record BridgeHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string,string> Headers,
    byte[] Body)
{
    public string? Header(string name)=>Headers.TryGetValue(name,out var value)?value:null;
}

internal sealed class BridgeHttpResponse
{
    public int StatusCode{get;set;}=200;
    public string? ContentType{get;set;}
    public Dictionary<string,string> Headers{get;}=new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body{get;set;}=[];
}

internal sealed class BridgeBindException:Exception
{
    public BridgeBindException(string message,Exception inner):base(message,inner){}
}

/// <summary>
/// Minimal one-request-per-connection HTTP/1.x front door for the loopback bridge.
/// It owns accepted sockets directly so connection pressure, header lifetime and entity-body
/// lifetime are bounded before an application handler is invoked.
/// </summary>
internal sealed class LoopbackBridgeServer:IDisposable
{
    private const int MaxHeaderBytes=16*1024;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _bodyTimeout;
    private bool _started;

    public LoopbackBridgeServer(int port,int maxConnections,TimeSpan headerTimeout,TimeSpan bodyTimeout)
    {
        if(port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        if(maxConnections<1)throw new ArgumentOutOfRangeException(nameof(maxConnections));
        _listener=new TcpListener(IPAddress.Loopback,port);
        _slots=new SemaphoreSlim(maxConnections,maxConnections);
        _headerTimeout=headerTimeout;
        _bodyTimeout=bodyTimeout;
    }

    public void Start()
    {
        try
        {
            _listener.Start(128);
            _started=true;
        }
        catch(SocketException e)
        {
            throw new BridgeBindException("Loopback bridge port could not be bound.",e);
        }
    }

    public async Task RunAsync(Func<BridgeHttpRequest,CancellationToken,Task<BridgeHttpResponse>> handler,CancellationToken ct)
    {
        if(!_started)throw new InvalidOperationException("Loopback bridge server has not been started.");
        var active=new List<Task>();
        try
        {
            while(!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client=await _listener.AcceptTcpClientAsync(ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
                catch(ObjectDisposedException) when(ct.IsCancellationRequested){break;}
                catch(SocketException) when(ct.IsCancellationRequested){break;}

                active.RemoveAll(t=>t.IsCompleted);
                if(!_slots.Wait(0))
                {
                    await RejectBusyAsync(client);
                    continue;
                }
                active.Add(HandleOwnedConnectionAsync(client,handler,ct));
            }
        }
        finally
        {
            var pending=active.Where(t=>!t.IsCompleted).ToArray();
            if(pending.Length>0)
            {
                try{await Task.WhenAll(pending);}catch(OperationCanceledException) when(ct.IsCancellationRequested){}
            }
        }
    }

    public void Stop()
    {
        try{_listener.Stop();}catch{}
        _started=false;
    }

    private async Task HandleOwnedConnectionAsync(
        TcpClient client,
        Func<BridgeHttpRequest,CancellationToken,Task<BridgeHttpResponse>> handler,
        CancellationToken ct)
    {
        using(client)
        {
            try
            {
                client.NoDelay=true;
                var stream=client.GetStream();
                var parsed=await ReadRequestAsync(stream,ct);
                if(parsed.Response is not null)
                {
                    await WriteResponseAsync(stream,parsed.Response,ct);
                    return;
                }

                var response=await handler(parsed.Request!,ct);
                await WriteResponseAsync(stream,response,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){}
            catch(IOException){}
            catch(SocketException){}
            catch
            {
                try
                {
                    var stream=client.GetStream();
                    await WriteResponseAsync(stream,Status(500),CancellationToken.None);
                }
                catch{}
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    private async Task<(BridgeHttpRequest? Request,BridgeHttpResponse? Response)> ReadRequestAsync(NetworkStream stream,CancellationToken ct)
    {
        byte[] received;
        int headerEnd;
        using(var headerCts=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headerCts.CancelAfter(_headerTimeout);
            using var buffer=new MemoryStream();
            var chunk=new byte[2048];
            try
            {
                while(true)
                {
                    var count=await stream.ReadAsync(chunk,headerCts.Token);
                    if(count<=0)return (null,Status(400));
                    buffer.Write(chunk,0,count);
                    if(buffer.Length>MaxHeaderBytes+4096)return (null,Status(431));
                    received=buffer.ToArray();
                    headerEnd=FindHeaderEnd(received);
                    if(headerEnd>=0)break;
                    if(buffer.Length>MaxHeaderBytes)return (null,Status(431));
                }
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {
                return (null,Status(408));
            }
        }

        received=received!;
        if(headerEnd>MaxHeaderBytes)return (null,Status(431));
        var headerText=Encoding.ASCII.GetString(received,0,headerEnd);
        if(!TryParseHead(headerText,out var method,out var path,out var headers))return (null,Status(400));

        if(headers.ContainsKey("Transfer-Encoding"))return (null,Status(411));

        long contentLength=0;
        if(headers.TryGetValue("Content-Length",out var contentLengthText))
        {
            if(!long.TryParse(contentLengthText,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out contentLength)||contentLength<0)
                return (null,Status(400));
        }
        else if(string.Equals(method,"POST",StringComparison.OrdinalIgnoreCase))
        {
            return (null,Status(411));
        }

        var maxBody=string.Equals(path,"/v1/preview",StringComparison.Ordinal)?262144L:8192L;
        if(contentLength>maxBody)return (null,Status(413));
        if(contentLength>int.MaxValue)return (null,Status(413));

        var body=new byte[(int)contentLength];
        var initialOffset=headerEnd+4;
        var initialAvailable=Math.Max(0,received.Length-initialOffset);
        var copied=Math.Min(initialAvailable,body.Length);
        if(copied>0)Buffer.BlockCopy(received,initialOffset,body,0,copied);

        if(copied<body.Length)
        {
            using var bodyCts=CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCts.CancelAfter(_bodyTimeout);
            try
            {
                while(copied<body.Length)
                {
                    var count=await stream.ReadAsync(body.AsMemory(copied,body.Length-copied),bodyCts.Token);
                    if(count<=0)return (null,Status(400));
                    copied+=count;
                }
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {
                return (null,Status(408));
            }
        }

        return (new BridgeHttpRequest(method,path,headers,body),null);
    }

    private static bool TryParseHead(string text,out string method,out string path,out Dictionary<string,string> headers)
    {
        method="";
        path="";
        headers=new(StringComparer.OrdinalIgnoreCase);
        var lines=text.Split("\r\n",StringSplitOptions.None);
        if(lines.Length<1)return false;
        var requestLine=lines[0].Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(requestLine.Length!=3||!requestLine[2].StartsWith("HTTP/1.",StringComparison.OrdinalIgnoreCase))return false;
        method=requestLine[0];
        path=NormalizePath(requestLine[1]);
        if(path.Length==0)return false;

        for(var i=1;i<lines.Length;i++)
        {
            var line=lines[i];
            if(line.Length==0)continue;
            if(char.IsWhiteSpace(line[0]))return false;
            var separator=line.IndexOf(':');
            if(separator<=0)return false;
            var name=line[..separator].Trim();
            var value=line[(separator+1)..].Trim();
            if(name.Length==0||headers.ContainsKey(name))return false;
            headers[name]=value;
        }
        return true;
    }

    private static string NormalizePath(string target)
    {
        if(Uri.TryCreate(target,UriKind.Absolute,out var absolute))return absolute.AbsolutePath;
        var query=target.IndexOf('?');
        var path=query>=0?target[..query]:target;
        return path.StartsWith('/')?path:"";
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for(var i=0;i<=bytes.Length-4;i++)
            if(bytes[i]==13&&bytes[i+1]==10&&bytes[i+2]==13&&bytes[i+3]==10)return i;
        return -1;
    }

    private static async Task RejectBusyAsync(TcpClient client)
    {
        using(client)
        {
            try
            {
                client.NoDelay=true;
                var response=Status(503);
                response.ContentType="application/json; charset=utf-8";
                response.Body=Encoding.UTF8.GetBytes("{\"success\":false,\"code\":\"bridge_busy\"}");
                await WriteResponseAsync(client.GetStream(),response,CancellationToken.None);
            }
            catch{}
        }
    }

    private static BridgeHttpResponse Status(int code)=>new(){StatusCode=code};

    private static async Task WriteResponseAsync(NetworkStream stream,BridgeHttpResponse response,CancellationToken ct)
    {
        var body=response.Body??[];
        var builder=new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(Reason(response.StatusCode)).Append("\r\n");
        builder.Append("Connection: close\r\n");
        builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if(!string.IsNullOrWhiteSpace(response.ContentType))builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        foreach(var header in response.Headers)
        {
            if(header.Key.Equals("Connection",StringComparison.OrdinalIgnoreCase)||
               header.Key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)||
               header.Key.Equals("Content-Type",StringComparison.OrdinalIgnoreCase))continue;
            builder.Append(header.Key).Append(": ").Append(header.Value.Replace("\r","").Replace("\n","")).Append("\r\n");
        }
        builder.Append("\r\n");
        var head=Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(head,ct);
        if(body.Length>0)await stream.WriteAsync(body,ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int code)=>code switch
    {
        200=>"OK",204=>"No Content",400=>"Bad Request",403=>"Forbidden",404=>"Not Found",
        405=>"Method Not Allowed",408=>"Request Timeout",411=>"Length Required",413=>"Content Too Large",
        415=>"Unsupported Media Type",422=>"Unprocessable Content",429=>"Too Many Requests",431=>"Request Header Fields Too Large",
        500=>"Internal Server Error",503=>"Service Unavailable",504=>"Gateway Timeout",_=>"Status"
    };

    public void Dispose()
    {
        Stop();
        _slots.Dispose();
    }
}
