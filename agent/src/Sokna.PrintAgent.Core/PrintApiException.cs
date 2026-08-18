using System.Net;
namespace Sokna.PrintAgent.Core;

public sealed class PrintApiException : HttpRequestException
{
    public string? Code { get; }
    public string? CurrentState { get; }
    public bool Terminal { get; }
    public bool RequiresHumanResolution { get; }
    public HttpStatusCode HttpStatus { get; }

    public PrintApiException(HttpStatusCode httpStatus,string message,string? code=null,string? currentState=null,bool terminal=false,bool requiresHumanResolution=false)
        : base(message,null,httpStatus)
    {
        HttpStatus=httpStatus;Code=code;CurrentState=currentState;Terminal=terminal;RequiresHumanResolution=requiresHumanResolution;
    }
}
