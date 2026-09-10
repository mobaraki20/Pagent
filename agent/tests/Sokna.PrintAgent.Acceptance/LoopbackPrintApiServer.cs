using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sokna.PrintAgent.Acceptance;

internal enum LoopbackResponseKind
{
    DisconnectAfterCommit,
    Success,
    Unauthorized,
    ProbeSuccess,
    BusinessFailure,
    MalformedJson,
    InvalidTypes,
    MismatchedReport
}

internal sealed record CapturedHttpRequest(string Method,string Target,string? Authorization,string Body);

/// <summary>
/// Minimal HTTP/1.1 loopback server used by acceptance tests. It deliberately sits below
/// HttpPrintTransport so the production serializer/auth/parser execute unchanged. It never
/// calls print/spool APIs. Response scripts can close the TCP connection after the request
/// body has been durably captured, reproducing a lost response after server commit.
/// </summary>
internal sealed class LoopbackPrintApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Queue<LoopbackResponseKind> _responses;
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _loop;
    private readonly object _gate=new();
    private readonly List<CapturedHttpRequest> _requests=[];

    public string BaseUrl { get; }
    public IReadOnlyList<CapturedHttpRequest> Requests { get { lock(_gate)return _requests.ToArray(); } }

    public LoopbackPrintApiServer(IEnumerable<LoopbackResponseKind> responses)
    {
        _responses=new Queue<LoopbackResponseKind>(responses);
        _listener=new TcpListener(IPAddress.Loopback,0);
        _listener.Start(16);
        var endpoint=(IPEndPoint)_listener.LocalEndpoint;
        BaseUrl=$"http://127.0.0.1:{endpoint.Port}";
        _loop=Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            TcpClient? client=null;
            try
            {
                client=await _listener.AcceptTcpClientAsync(_stop.Token);
                await HandleAsync(client,_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(ObjectDisposedException) when(_stop.IsCancellationRequested){break;}
            finally{client?.Dispose();}
        }
    }

    private async Task HandleAsync(TcpClient client,CancellationToken ct)
    {
        client.NoDelay=true;
        await using var stream=client.GetStream();
        var headerBytes=await ReadUntilAsync(stream,"\r\n\r\n"u8.ToArray(),64*1024,ct);
        var headerText=Encoding.ASCII.GetString(headerBytes);
        var lines=headerText.Split("\r\n",StringSplitOptions.None);
        var requestLine=lines.FirstOrDefault()??throw new InvalidDataException("HTTP request line missing.");
        var parts=requestLine.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length<2)throw new InvalidDataException("HTTP request line invalid.");
        var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in lines.Skip(1))
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            var colon=line.IndexOf(':');
            if(colon<=0)continue;
            headers[line[..colon].Trim()]=line[(colon+1)..].Trim();
        }

        byte[] bodyBytes=[];
        if(headers.TryGetValue("Content-Length",out var rawLength)&&int.TryParse(rawLength,NumberStyles.None,CultureInfo.InvariantCulture,out var length)&&length>0)
        {
            bodyBytes=await ReadExactAsync(stream,length,ct);
        }
        else if(headers.TryGetValue("Transfer-Encoding",out var transfer)&&transfer.Contains("chunked",StringComparison.OrdinalIgnoreCase))
        {
            bodyBytes=await ReadChunkedAsync(stream,ct);
        }

        var body=Encoding.UTF8.GetString(bodyBytes);
        headers.TryGetValue("Authorization",out var authorization);
        lock(_gate)_requests.Add(new CapturedHttpRequest(parts[0],parts[1],authorization,body));

        LoopbackResponseKind response;
        lock(_gate)response=_responses.Count>0?_responses.Dequeue():LoopbackResponseKind.Success;
        if(response==LoopbackResponseKind.DisconnectAfterCommit)
        {
            return;
        }

        var payload=response switch
        {
            LoopbackResponseKind.Unauthorized=>"{\"success\":false,\"code\":\"invalid_token\",\"message\":\"invalid token\"}",
            LoopbackResponseKind.ProbeSuccess=>"{\"success\":true,\"protocol_version\":4,\"minimum_agent_version\":\"6.0.0\",\"recommended_agent_version\":\"6.2.0\",\"destinations\":[],\"capabilities\":[\"attempt_status\"],\"server_instance_id\":\"acceptance-server\",\"server_time\":\"2026-09-10T08:00:00Z\"}",
            LoopbackResponseKind.BusinessFailure=>"{\"success\":false,\"code\":\"destination_forbidden\",\"message\":\"business rejected\"}",
            LoopbackResponseKind.MalformedJson=>"{not-json",
            LoopbackResponseKind.InvalidTypes=>"{\"success\":\"yes\",\"attempt_id\":\"not-a-number\",\"local_receipt_id\":17}",
            LoopbackResponseKind.MismatchedReport=>"{\"success\":true,\"status\":\"submitted\",\"attempt_id\":999999,\"job_id\":999999,\"local_receipt_id\":\"wrong\"}",
            _=>BuildSuccess(body)
        };
        var status=response==LoopbackResponseKind.Unauthorized?"401 Unauthorized":"200 OK";
        await WriteResponseAsync(stream,status,payload,ct);
    }

    private static string BuildSuccess(string requestBody)
    {
        try
        {
            using var doc=JsonDocument.Parse(requestBody);
            var root=doc.RootElement;
            var attempt=root.TryGetProperty("attempt_id",out var a)&&a.TryGetInt64(out var av)?av:0;
            var receipt=root.TryGetProperty("local_receipt_id",out var r)&&r.ValueKind==JsonValueKind.String?r.GetString():null;
            var status=root.TryGetProperty("status",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString():null;
            return JsonSerializer.Serialize(new{success=true,status,attempt_id=attempt,local_receipt_id=receipt});
        }
        catch{return "{\"success\":true}";}
    }

    private static async Task WriteResponseAsync(NetworkStream stream,string status,string payload,CancellationToken ct)
    {
        var bytes=Encoding.UTF8.GetBytes(payload);
        var headers=$"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),ct);
        await stream.WriteAsync(bytes,ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadUntilAsync(NetworkStream stream,byte[] delimiter,int maxBytes,CancellationToken ct)
    {
        using var output=new MemoryStream();
        var matched=0;
        var one=new byte[1];
        while(output.Length<maxBytes)
        {
            var read=await stream.ReadAsync(one,ct);
            if(read==0)throw new EndOfStreamException("HTTP headers ended early.");
            output.WriteByte(one[0]);
            if(one[0]==delimiter[matched])
            {
                matched++;
                if(matched==delimiter.Length)return output.ToArray();
            }
            else matched=one[0]==delimiter[0]?1:0;
        }
        throw new InvalidDataException("HTTP headers exceed acceptance fixture limit.");
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream,int length,CancellationToken ct)
    {
        var result=new byte[length];
        var offset=0;
        while(offset<length)
        {
            var read=await stream.ReadAsync(result.AsMemory(offset,length-offset),ct);
            if(read==0)throw new EndOfStreamException("HTTP body ended early.");
            offset+=read;
        }
        return result;
    }

    private static async Task<byte[]> ReadChunkedAsync(NetworkStream stream,CancellationToken ct)
    {
        using var output=new MemoryStream();
        while(true)
        {
            var line=await ReadAsciiLineAsync(stream,ct);
            var semicolon=line.IndexOf(';');
            var sizeText=(semicolon>=0?line[..semicolon]:line).Trim();
            if(!int.TryParse(sizeText,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var size)||size<0)
                throw new InvalidDataException("Invalid HTTP chunk size.");
            if(size==0)
            {
                while((await ReadAsciiLineAsync(stream,ct)).Length>0){}
                break;
            }
            var chunk=await ReadExactAsync(stream,size,ct);
            await output.WriteAsync(chunk,ct);
            var crlf=await ReadExactAsync(stream,2,ct);
            if(crlf[0]!='\r'||crlf[1]!='\n')throw new InvalidDataException("Invalid HTTP chunk terminator.");
        }
        return output.ToArray();
    }

    private static async Task<string> ReadAsciiLineAsync(NetworkStream stream,CancellationToken ct)
    {
        using var output=new MemoryStream();
        var one=new byte[1];
        var previous=-1;
        while(output.Length<8192)
        {
            var read=await stream.ReadAsync(one,ct);
            if(read==0)throw new EndOfStreamException("HTTP line ended early.");
            if(previous=='\r'&&one[0]=='\n')
            {
                var bytes=output.ToArray();
                return Encoding.ASCII.GetString(bytes,0,Math.Max(0,bytes.Length-1));
            }
            output.WriteByte(one[0]);
            previous=one[0];
        }
        throw new InvalidDataException("HTTP line too long.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try{await _loop;}catch(OperationCanceledException){}
        _stop.Dispose();
    }
}
