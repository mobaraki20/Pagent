namespace Sokna.PrintAgent.Core;

/// <summary>
/// Agent-owned virtual destinations that participate in the same server routing pipeline as
/// Windows printer queues, but do not depend on Windows Spooler device discovery.
/// </summary>
public static class VirtualPrinterQueues
{
    public const string PdfTestQueueName="Sokna PDF Test (TEST ONLY, 203 DPI)";
    public const string PdfTestDriver="Sokna Internal PDF Test Sink";
    public const string PdfTestPort="SOKNA-PDF";

    public static bool IsPdfTestQueue(string? name)
        =>string.Equals(name,PdfTestQueueName,StringComparison.OrdinalIgnoreCase);

    public static PrinterQueueHealth PdfTestHealth()
        =>new(PdfTestQueueName,false,false,false,false,0,PdfTestDriver,PdfTestPort);

    public static IReadOnlyList<PrinterQueueHealth> Merge(IEnumerable<PrinterQueueHealth>? physicalQueues)
    {
        var result=(physicalQueues??[])
            .Where(x=>!IsPdfTestQueue(x.Name))
            .ToList();
        result.Add(PdfTestHealth());
        return result;
    }
}
