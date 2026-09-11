using System.Globalization;
using System.IO.Compression;
using System.Text;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Worker;

/// <summary>
/// Deterministic, unattended test destination. It exercises the real Claim/Accept/Start/Worker/
/// durable Report path, but commits the thermal raster to a PDF under ProgramData instead of
/// submitting a Windows spooler job. It intentionally does not automate Microsoft Print to PDF.
/// </summary>
public sealed class PdfTestSinkAdapter:IPrinterAdapter
{
    internal const int Dpi=203;

    public async Task<WorkerResult> SubmitAsync(WorkerInput input,CancellationToken ct)
    {
        var fenced=false;
        try
        {
            if(!VirtualPrinterQueues.IsPdfTestQueue(input.QueueName))
                return Failed(input,"pdf_test_queue_mismatch","PDF Test Sink فقط Queue مجازی اختصاصی Sokna را می‌پذیرد.");
            if(input.Copies is <1 or >5)
                return Failed(input,"invalid_copies","Copies باید بین 1 و 5 باشد.");
            if(input.PaperWidthMm is <40 or >120||input.PrintableWidthMm<=0||input.PrintableWidthMm>input.PaperWidthMm)
                return Failed(input,"invalid_pdf_geometry","هندسه کاغذ PDF Test Sink معتبر نیست.");

            ct.ThrowIfCancellationRequested();
            using var bitmap=ReceiptRenderer.Render(input.PayloadJson,input.PrintableWidthMm,input.PaperWidthMm,Dpi,Dpi);
            var pdf=ReceiptPdfWriter.Build(bitmap,input.PaperWidthMm,input.PrintableWidthMm,input.Copies,Dpi);
            var outputDirectory=ResolveOutputDirectory(input.ResultPath);
            Directory.CreateDirectory(outputDirectory);
            var fileName=$"Sokna-job-{input.ServerJobId}-attempt-{input.AttemptId}.pdf";
            var outputPath=Path.Combine(outputDirectory,fileName);

            // Same safety boundary as a physical spooler submission: after the fence, a crash may
            // have committed an externally visible artifact. Never turn such ambiguity into an
            // automatic retry.
            await DurableFile.TouchAtomicAsync(
                input.FencePath,
                $"pdf:{input.ServerJobId}:{input.AttemptId}:{input.ContentSha256}",
                CancellationToken.None);
            fenced=true;

            var temp=outputPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await using(var stream=new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64*1024,
                    FileOptions.Asynchronous|FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(pdf,CancellationToken.None);
                    await stream.FlushAsync(CancellationToken.None);
                    stream.Flush(true);
                }
                File.Move(temp,outputPath,false);
            }
            finally
            {
                try{if(File.Exists(temp))File.Delete(temp);}catch{}
            }

            return new WorkerResult(
                input.ServerJobId,
                input.AttemptId,
                input.LocalReceiptId,
                input.ContentSha256,
                "submitted",
                "pdf:"+fileName);
        }
        catch(Exception e)
        {
            var message=Safe(e.Message);
            return fenced
                ?new WorkerResult(input.ServerJobId,input.AttemptId,input.LocalReceiptId,input.ContentSha256,"unknown",null,false,"pdf_sink_ambiguity",message)
                :Failed(input,"pdf_render_or_pre_submit_failed",message);
        }
    }

    internal static string ResolveOutputDirectory(string resultPath)
    {
        var result=Path.GetFullPath(resultPath);
        var work=Path.GetDirectoryName(result)??throw new InvalidDataException("Worker result path فاقد directory است.");
        if(!string.Equals(Path.GetFileName(work),"work",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PDF Test Sink فقط layout استاندارد ProgramData/work را می‌پذیرد.");
        var dataRoot=Directory.GetParent(work)?.FullName??throw new InvalidDataException("ProgramData root برای PDF Test Sink قابل تشخیص نیست.");
        return Path.Combine(dataRoot,"TestPrints");
    }

    private static WorkerResult Failed(WorkerInput input,string code,string message)
        =>new(input.ServerJobId,input.AttemptId,input.LocalReceiptId,input.ContentSha256,"failed",null,true,code,Safe(message));

    private static string Safe(string value)=>value.Length>400?value[..400]:value;
}

internal static class ReceiptPdfWriter
{
    private static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;

    public static byte[] Build(System.Drawing.Bitmap bitmap,double paperWidthMm,double printableWidthMm,int copies,int dpi)
    {
        if(bitmap.Width<=0||bitmap.Height<=0)throw new InvalidDataException("PDF raster geometry نامعتبر است.");
        if(copies is <1 or >5)throw new InvalidDataException("PDF copies خارج از بازه مجاز است.");
        if(dpi<=0)throw new InvalidDataException("PDF DPI نامعتبر است.");

        var expectedPixelWidth=(int)Math.Round(printableWidthMm/25.4d*dpi);
        if(bitmap.Width!=expectedPixelWidth)throw new InvalidDataException("PDF raster width با RenderProfile مجازی 203 DPI تطابق ندارد.");
        var pageWidthPoints=paperWidthMm/25.4d*72d;
        var imageWidthPoints=bitmap.Width/(double)dpi*72d;
        var pageHeightPoints=bitmap.Height/(double)dpi*72d;
        if(pageWidthPoints<=0||imageWidthPoints<=0||imageWidthPoints>pageWidthPoints||pageHeightPoints<=0||pageHeightPoints>14000)
            throw new InvalidDataException("ابعاد صفحه PDF خارج از محدوده امن است.");
        var xPoints=(pageWidthPoints-imageWidthPoints)/2d;

        var dib=WinspoolAdapter.CreateMonochromePrinterDib(bitmap);
        var packedRowBytes=(dib.Width+7)/8;
        var raw=new byte[checked(packedRowBytes*dib.Height)];
        for(var y=0;y<dib.Height;y++)
            Buffer.BlockCopy(dib.Bits,y*dib.Stride,raw,y*packedRowBytes,packedRowBytes);
        var compressed=Compress(raw);

        using var output=new MemoryStream();
        WriteAscii(output,"%PDF-1.4\n");
        output.WriteByte((byte)'%');output.WriteByte(0xFF);output.WriteByte(0xFF);output.WriteByte(0xFF);output.WriteByte(0xFF);output.WriteByte((byte)'\n');

        var maxObjectId=3+copies*2;
        var offsets=new long[maxObjectId+1];
        WriteObject(output,offsets,1,"<< /Type /Catalog /Pages 2 0 R >>");
        var kids=string.Join(' ',Enumerable.Range(0,copies).Select(i=>$"{4+i*2} 0 R"));
        WriteObject(output,offsets,2,$"<< /Type /Pages /Kids [{kids}] /Count {copies} >>");
        WriteStreamObject(
            output,
            offsets,
            3,
            $"<< /Type /XObject /Subtype /Image /Width {dib.Width} /Height {dib.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode /Length {compressed.Length} >>",
            compressed);

        for(var copy=0;copy<copies;copy++)
        {
            var pageId=4+copy*2;
            var contentId=pageId+1;
            var content=$"q\n{F(imageWidthPoints)} 0 0 {F(pageHeightPoints)} {F(xPoints)} 0 cm\n/Im0 Do\nQ\n";
            var contentBytes=Encoding.ASCII.GetBytes(content);
            WriteObject(
                output,
                offsets,
                pageId,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageWidthPoints)} {F(pageHeightPoints)}] /Resources << /XObject << /Im0 3 0 R >> >> /Contents {contentId} 0 R >>");
            WriteStreamObject(output,offsets,contentId,$"<< /Length {contentBytes.Length} >>",contentBytes);
        }

        var xref=output.Position;
        WriteAscii(output,$"xref\n0 {maxObjectId+1}\n");
        WriteAscii(output,"0000000000 65535 f \n");
        for(var id=1;id<=maxObjectId;id++)
            WriteAscii(output,$"{offsets[id]:D10} 00000 n \n");
        WriteAscii(output,$"trailer\n<< /Size {maxObjectId+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] Compress(byte[] raw)
    {
        using var buffer=new MemoryStream();
        using(var zlib=new ZLibStream(buffer,CompressionLevel.Optimal,true))zlib.Write(raw,0,raw.Length);
        return buffer.ToArray();
    }

    private static void WriteObject(Stream stream,long[] offsets,int id,string body)
    {
        offsets[id]=stream.Position;
        WriteAscii(stream,$"{id} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteStreamObject(Stream stream,long[] offsets,int id,string dictionary,byte[] data)
    {
        offsets[id]=stream.Position;
        WriteAscii(stream,$"{id} 0 obj\n{dictionary}\nstream\n");
        stream.Write(data,0,data.Length);
        WriteAscii(stream,"\nendstream\nendobj\n");
    }

    private static void WriteAscii(Stream stream,string value)
    {
        var bytes=Encoding.ASCII.GetBytes(value);
        stream.Write(bytes,0,bytes.Length);
    }

    private static string F(double value)=>value.ToString("0.###",Invariant);
}
