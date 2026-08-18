using System.Runtime.InteropServices;
namespace Sokna.PrintAgent.Core;
public sealed class WindowsPrinterHealthProvider:IPrinterHealthProvider
{
    private const uint PRINTER_ENUM_LOCAL=2,PRINTER_ENUM_CONNECTIONS=4;
    private const uint PAUSED=0x00000001,ERROR=0x00000002,PENDING_DELETION=0x00000004,PAPER_JAM=0x00000008,PAPER_OUT=0x00000010,PAPER_PROBLEM=0x00000040,OFFLINE=0x00000080,OUTPUT_BIN_FULL=0x00000800,NOT_AVAILABLE=0x00001000,NO_TONER=0x00040000,PAGE_PUNT=0x00080000,USER_INTERVENTION=0x00100000,OUT_OF_MEMORY=0x00200000,DOOR_OPEN=0x00400000,SERVER_UNKNOWN=0x00800000;
    private const uint BLOCKING_ERROR_MASK=ERROR|PENDING_DELETION|PAPER_JAM|PAPER_PROBLEM|OUTPUT_BIN_FULL|NOT_AVAILABLE|NO_TONER|PAGE_PUNT|USER_INTERVENTION|OUT_OF_MEMORY|DOOR_OPEN|SERVER_UNKNOWN;
    public IReadOnlyList<PrinterQueueHealth> GetQueues()
    {
        if(!OperatingSystem.IsWindows())return [];
        uint needed=0,returned=0;EnumPrinters(PRINTER_ENUM_LOCAL|PRINTER_ENUM_CONNECTIONS,null,2,IntPtr.Zero,0,out needed,out returned);if(needed==0)return [];
        var ptr=Marshal.AllocHGlobal((int)needed);
        try
        {
            if(!EnumPrinters(PRINTER_ENUM_LOCAL|PRINTER_ENUM_CONNECTIONS,null,2,ptr,needed,out needed,out returned))return [];
            var size=Marshal.SizeOf<PRINTER_INFO_2>();var list=new List<PrinterQueueHealth>();
            for(var i=0;i<returned;i++)
            {
                var info=Marshal.PtrToStructure<PRINTER_INFO_2>(IntPtr.Add(ptr,i*size));var name=Marshal.PtrToStringUni(info.pPrinterName)??"";if(name.Length==0)continue;
                list.Add(new(name,(info.Status&OFFLINE)!=0,(info.Status&PAUSED)!=0,(info.Status&PAPER_OUT)!=0,(info.Status&BLOCKING_ERROR_MASK)!=0,(int)info.cJobs,Marshal.PtrToStringUni(info.pDriverName)??"",Marshal.PtrToStringUni(info.pPortName)??""));
            }
            return list;
        }
        finally{Marshal.FreeHGlobal(ptr);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct PRINTER_INFO_2{public IntPtr pServerName,pPrinterName,pShareName,pPortName,pDriverName,pComment,pLocation,pDevMode,pSepFile,pPrintProcessor,pDatatype,pParameters,pSecurityDescriptor;public uint Attributes,Priority,DefaultPriority,StartTime,UntilTime,Status,cJobs,AveragePPM;}
    [DllImport("winspool.drv",SetLastError=true,CharSet=CharSet.Unicode)]private static extern bool EnumPrinters(uint flags,string? name,uint level,IntPtr pPrinterEnum,uint cbBuf,out uint pcbNeeded,out uint pcReturned);
}
