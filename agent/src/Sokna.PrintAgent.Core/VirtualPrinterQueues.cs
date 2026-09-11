namespace Sokna.PrintAgent.Core;

/// <summary>
/// Agent-owned virtual destinations that can participate in the same server routing pipeline as
/// Windows printer queues. The PDF destination is intentionally opt-in and must not appear in
/// production discovery unless explicit UAT/test mode is enabled.
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

    /// <summary>Explicit merge used by tests/UAT when the PDF sink is intentionally enabled.</summary>
    public static IReadOnlyList<PrinterQueueHealth> Merge(IEnumerable<PrinterQueueHealth>? physicalQueues)
    {
        var result=(physicalQueues??[])
            .Where(x=>!IsPdfTestQueue(x.Name))
            .ToList();
        result.Add(PdfTestHealth());
        return result;
    }

    /// <summary>
    /// Production discovery surface. When PDF test mode is off, any stale/spoofed virtual queue
    /// is removed and only physical Windows queues are returned.
    /// </summary>
    public static IReadOnlyList<PrinterQueueHealth> ForDiscovery(IEnumerable<PrinterQueueHealth>? queues,bool pdfTestEnabled)
    {
        var physical=(queues??[])
            .Where(x=>!IsPdfTestQueue(x.Name))
            .ToList();
        return pdfTestEnabled?Merge(physical):physical;
    }
}
