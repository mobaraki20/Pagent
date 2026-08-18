using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sokna.PrintAgent.Core;
namespace Sokna.PrintAgent.Worker;

public sealed class WinspoolAdapter:IPrinterAdapter
{
    private const int HORZRES=8,LOGPIXELSX=88,LOGPIXELSY=90;
    public async Task<WorkerResult> SubmitAsync(WorkerInput input,CancellationToken ct)
    {
        try
        {
            if(input.Copies is <1 or >5)return Failed(input,"invalid_copies","Copies باید بین 1 و 5 باشد.");
            using var bitmap=ReceiptRenderer.Render(input.PayloadJson,input.PrintableWidthMm,input.PaperWidthMm);
            var hdc=CreateDC("WINSPOOL",input.QueueName,null,IntPtr.Zero);if(hdc==IntPtr.Zero)return Failed(input,"printer_open_failed",Win32Error());
            try
            {
                var dpiX=GetDeviceCaps(hdc,LOGPIXELSX);var dpiY=GetDeviceCaps(hdc,LOGPIXELSY);var deviceWidth=GetDeviceCaps(hdc,HORZRES);
                if(dpiX<=0||dpiY<=0||deviceWidth<=0)return Failed(input,"invalid_printer_geometry","Printer Queue هندسه چاپ قابل‌اتکا ارائه نکرد.");
                var targetWidth=(int)Math.Round(input.PrintableWidthMm/25.4*dpiX);
                if(targetWidth<32||targetWidth>deviceWidth)return Failed(input,"printable_width_exceeds_device",$"عرض درخواستی {input.PrintableWidthMm:0.#}mm با Printable Area این Queue سازگار نیست.");
                var targetHeight=Math.Max(1,(int)Math.Round(bitmap.Height*(targetWidth/(double)bitmap.Width)));
                var x=Math.Max(0,(deviceWidth-targetWidth)/2);
                var doc=new DOCINFO{cbSize=Marshal.SizeOf<DOCINFO>(),lpszDocName=$"Sokna {input.ServerJobId} / {input.AttemptId}",lpszOutput=null,lpszDatatype=null,fwType=0};
                ct.ThrowIfCancellationRequested();
                // Durable Submission Fence: after all deterministic render/device checks and immediately before
                // StartDoc, the first operation that can create a Windows spooler job. Once this file exists,
                // a crash without a durable WorkerResult must never trigger automatic reprint.
                await DurableFile.TouchAtomicAsync(input.FencePath,$"{input.ServerJobId}:{input.AttemptId}:{input.ContentSha256}",CancellationToken.None);
                var spoolerJobId=StartDoc(hdc,ref doc);if(spoolerJobId<=0)return Failed(input,"start_doc_failed",Win32Error());
                try
                {
                    for(var copy=0;copy<Math.Max(1,input.Copies);copy++)
                    {
                        if(StartPage(hdc)<=0)throw new InvalidOperationException("StartPage failed: "+Win32Error());
                        DrawBitmap(hdc,bitmap,x,targetWidth,targetHeight,dpiX,dpiY);
                        if(EndPage(hdc)<=0)throw new InvalidOperationException("EndPage failed: "+Win32Error());
                    }
                    if(EndDoc(hdc)<=0)throw new InvalidOperationException("EndDoc failed: "+Win32Error());
                    return new WorkerResult(input.ServerJobId,input.AttemptId,input.LocalReceiptId,input.ContentSha256,"submitted",spoolerJobId.ToString());
                }
                catch(Exception e)
                {
                    try{AbortDoc(hdc);}catch{}
                    // StartDoc succeeded: Windows may already own some/all pages. Automatic retry is forbidden.
                    return new WorkerResult(input.ServerJobId,input.AttemptId,input.LocalReceiptId,input.ContentSha256,"unknown",spoolerJobId.ToString(),false,"spooler_ambiguity",Safe(e.Message));
                }
            }
            finally{DeleteDC(hdc);}
        }
        catch(Exception e){return Failed(input,"render_or_pre_submit_failed",Safe(e.Message));}
    }

    private static WorkerResult Failed(WorkerInput i,string code,string message)=>new(i.ServerJobId,i.AttemptId,i.LocalReceiptId,i.ContentSha256,"failed",null,true,code,Safe(message));
    private static string Safe(string s)=>s.Length>400?s[..400]:s;
    private static string Win32Error()=>new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

    private static void DrawBitmap(IntPtr hdc,Bitmap bitmap,int x,int targetWidth,int targetHeight,int dpiX,int dpiY)
    {
        using var clone=new Bitmap(bitmap.Width,bitmap.Height,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(clone)){g.DrawImageUnscaled(bitmap,0,0);}
        var rect=new Rectangle(0,0,clone.Width,clone.Height);var data=clone.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            var bmi=new BITMAPINFO{bmiHeader=new BITMAPINFOHEADER{biSize=(uint)Marshal.SizeOf<BITMAPINFOHEADER>(),biWidth=clone.Width,biHeight=-clone.Height,biPlanes=1,biBitCount=32,biCompression=0,biSizeImage=(uint)(Math.Abs(data.Stride)*clone.Height)}};
            var copied=StretchDIBits(hdc,x,0,targetWidth,targetHeight,0,0,clone.Width,clone.Height,data.Scan0,ref bmi,0,0x00CC0020);
            if(copied==0)throw new InvalidOperationException($"StretchDIBits failed at {dpiX}x{dpiY} DPI: {Win32Error()}");
        }
        finally{clone.UnlockBits(data);}
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DOCINFO{public int cbSize;[MarshalAs(UnmanagedType.LPWStr)]public string lpszDocName;[MarshalAs(UnmanagedType.LPWStr)]public string? lpszOutput;[MarshalAs(UnmanagedType.LPWStr)]public string? lpszDatatype;public int fwType;}
    [StructLayout(LayoutKind.Sequential)]private struct BITMAPINFOHEADER{public uint biSize;public int biWidth,biHeight;public ushort biPlanes,biBitCount;public uint biCompression,biSizeImage;public int biXPelsPerMeter,biYPelsPerMeter;public uint biClrUsed,biClrImportant;}
    [StructLayout(LayoutKind.Sequential)]private struct BITMAPINFO{public BITMAPINFOHEADER bmiHeader;public uint bmiColors;}
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateDC(string driver,string device,string? output,IntPtr devmode);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern int StartDoc(IntPtr hdc,ref DOCINFO lpdi);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int EndDoc(IntPtr hdc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int AbortDoc(IntPtr hdc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int StartPage(IntPtr hdc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int EndPage(IntPtr hdc);
    [DllImport("gdi32.dll")]private static extern int GetDeviceCaps(IntPtr hdc,int index);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int StretchDIBits(IntPtr hdc,int xDest,int yDest,int DestWidth,int DestHeight,int xSrc,int ySrc,int SrcWidth,int SrcHeight,IntPtr bits,ref BITMAPINFO bitsInfo,uint iUsage,uint rop);
}
