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
            var hdc=CreateDC("WINSPOOL",input.QueueName,null,IntPtr.Zero);if(hdc==IntPtr.Zero)return Failed(input,"printer_open_failed",Win32Error());
            try
            {
                var dpiX=GetDeviceCaps(hdc,LOGPIXELSX);var dpiY=GetDeviceCaps(hdc,LOGPIXELSY);var deviceWidth=GetDeviceCaps(hdc,HORZRES);
                if(dpiX<=0||dpiY<=0||deviceWidth<=0)return Failed(input,"invalid_printer_geometry","Printer Queue هندسه چاپ قابل‌اتکا ارائه نکرد.");
                var targetWidth=(int)Math.Round(input.PrintableWidthMm/25.4*dpiX);
                if(targetWidth<32||targetWidth>deviceWidth)return Failed(input,"printable_width_exceeds_device",$"عرض درخواستی {input.PrintableWidthMm:0.#}mm با Printable Area این Queue سازگار نیست.");
                // Render directly in the queue's native device pixels. Rendering at a fixed 203 DPI and
                // stretching here makes anti-aliased text look like a low-quality photo on a thermal head.
                using var bitmap=ReceiptRenderer.Render(input.PayloadJson,input.PrintableWidthMm,input.PaperWidthMm,dpiX,dpiY);
                if(bitmap.Width!=targetWidth)return Failed(input,"renderer_geometry_mismatch","عرض تصویر تولیدشده با هندسه Queue یکسان نیست.");
                var targetHeight=bitmap.Height;
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
        if(bitmap.Width!=targetWidth||bitmap.Height!=targetHeight)throw new InvalidOperationException("Printer bitmap must be submitted at native 1:1 dimensions.");
        var dib=CreateMonochromePrinterDib(bitmap);
        var bmi=new MONO_BITMAPINFO
        {
            bmiHeader=new BITMAPINFOHEADER{biSize=(uint)Marshal.SizeOf<BITMAPINFOHEADER>(),biWidth=dib.Width,biHeight=-dib.Height,biPlanes=1,biBitCount=1,biCompression=0,biSizeImage=(uint)dib.Bits.Length},
            black=0x00000000,
            white=0x00FFFFFF,
        };
        var handle=GCHandle.Alloc(dib.Bits,GCHandleType.Pinned);
        try
        {
            var copied=SetDIBitsToDevice(hdc,x,0,(uint)dib.Width,(uint)dib.Height,0,0,0,(uint)dib.Height,handle.AddrOfPinnedObject(),ref bmi,0);
            if(copied==0)throw new InvalidOperationException($"SetDIBitsToDevice failed at {dpiX}x{dpiY} DPI: {Win32Error()}");
        }
        finally{handle.Free();}
    }

    internal static Bitmap CreateOpaquePrinterDib(Bitmap source)
    {
        var dib=new Bitmap(source.Width,source.Height,PixelFormat.Format24bppRgb);
        dib.SetResolution(source.HorizontalResolution,source.VerticalResolution);
        using var g=Graphics.FromImage(dib);
        g.Clear(Color.White);
        g.DrawImageUnscaled(source,0,0);
        return dib;
    }

    internal sealed record MonochromePrinterDib(int Width,int Height,int Stride,byte[] Bits)
    {
        public bool IsWhite(int x,int y)=>(Bits[y*Stride+x/8]&(0x80>>(x%8)))!=0;
    }

    internal static MonochromePrinterDib CreateMonochromePrinterDib(Bitmap source)
    {
        using var opaque=CreateOpaquePrinterDib(source);
        var stride=((opaque.Width+31)/32)*4;
        var bits=Enumerable.Repeat((byte)0xFF,stride*opaque.Height).ToArray();
        var rectangle=new Rectangle(0,0,opaque.Width,opaque.Height);
        var data=opaque.LockBits(rectangle,ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try
        {
            var sourceStride=Math.Abs(data.Stride);var row=new byte[sourceStride];
            for(var y=0;y<opaque.Height;y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,sourceStride);
                for(var x=0;x<opaque.Width;x++)
                {
                    var offset=x*3;var luminance=row[offset]+row[offset+1]+row[offset+2];
                    if(luminance<384)bits[y*stride+x/8]&=(byte)~(0x80>>(x%8));
                }
            }
        }
        finally{opaque.UnlockBits(data);}
        return new MonochromePrinterDib(opaque.Width,opaque.Height,stride,bits);
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DOCINFO{public int cbSize;[MarshalAs(UnmanagedType.LPWStr)]public string lpszDocName;[MarshalAs(UnmanagedType.LPWStr)]public string? lpszOutput;[MarshalAs(UnmanagedType.LPWStr)]public string? lpszDatatype;public int fwType;}
    [StructLayout(LayoutKind.Sequential)]private struct BITMAPINFOHEADER{public uint biSize;public int biWidth,biHeight;public ushort biPlanes,biBitCount;public uint biCompression,biSizeImage;public int biXPelsPerMeter,biYPelsPerMeter;public uint biClrUsed,biClrImportant;}
    [StructLayout(LayoutKind.Sequential)]private struct MONO_BITMAPINFO{public BITMAPINFOHEADER bmiHeader;public uint black,white;}
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateDC(string driver,string device,string? output,IntPtr devmode);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern int StartDoc(IntPtr hdc,ref DOCINFO lpdi);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int EndDoc(IntPtr hdc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int AbortDoc(IntPtr hdc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int StartPage(IntPtr hdc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int EndPage(IntPtr hdc);
    [DllImport("gdi32.dll")]private static extern int GetDeviceCaps(IntPtr hdc,int index);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int SetDIBitsToDevice(IntPtr hdc,int xDest,int yDest,uint width,uint height,int xSrc,int ySrc,uint startScan,uint scanLines,IntPtr bits,ref MONO_BITMAPINFO bitsInfo,uint colorUse);
}
