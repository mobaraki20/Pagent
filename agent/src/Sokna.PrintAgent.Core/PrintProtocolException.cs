namespace Sokna.PrintAgent.Core;

/// <summary>
/// A syntactically or structurally invalid successful HTTP response from the Print API.
/// This is a contract/reconciliation fault, not proof that the network operation was not applied.
/// </summary>
public sealed class PrintProtocolException : Exception
{
    public string Code { get; }
    public string Action { get; }

    public PrintProtocolException(string action,string code,string message,Exception? inner=null)
        : base(message,inner)
    {
        Action=action;
        Code=code;
    }
}
