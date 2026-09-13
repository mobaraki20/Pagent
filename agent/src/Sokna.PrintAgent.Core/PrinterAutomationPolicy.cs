namespace Sokna.PrintAgent.Core;

/// <summary>Fail-closed eligibility policy for unattended printing by LocalSystem.</summary>
public static class PrinterAutomationPolicy
{
    private static readonly string[] InteractivePorts=["PORTPROMPT:","FILE:","SHRFAX:","NUL:"];

    public static bool IsCapable(PrinterQueueHealth queue)
    {
        if(VirtualPrinterQueues.IsPdfTestQueue(queue.Name))return true;
        var port=(queue.Port??string.Empty).Trim();
        if(InteractivePorts.Any(x=>string.Equals(port,x,StringComparison.OrdinalIgnoreCase)))return false;
        if(queue.Name.Contains("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase))return false;
        if(queue.Name.Contains("Microsoft XPS Document Writer",StringComparison.OrdinalIgnoreCase))return false;
        // Unknown monitor ports (for example third-party PDF drivers) are not assumed safe.
        return port.StartsWith("USB",StringComparison.OrdinalIgnoreCase)
            ||port.StartsWith("WSD",StringComparison.OrdinalIgnoreCase)
            ||port.StartsWith("IP_",StringComparison.OrdinalIgnoreCase)
            ||port.StartsWith("TCP",StringComparison.OrdinalIgnoreCase)
            ||port.StartsWith("LPT",StringComparison.OrdinalIgnoreCase)
            ||port.StartsWith("COM",StringComparison.OrdinalIgnoreCase)
            ||port.StartsWith("\\\\",StringComparison.Ordinal);
    }

    public static bool IsReady(PrinterQueueHealth queue)
        =>IsCapable(queue)&&!queue.Offline&&!queue.Paused&&!queue.PaperOut&&!queue.Error;
}
