using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Sokna.PrintAgent.Core;
using Sokna.PrintAgent.Worker;

var failures=new List<string>();
void Check(bool value,string name){if(!value)failures.Add(name);}
async Task ExpectThrowsAsync(Func<Task> action,string name){try{await action();failures.Add(name);}catch{}}

using var source=new Bitmap(80,60,PixelFormat.Format32bppArgb);source.SetPixel(40,30,Color.FromArgb(128,0,0,0));using var dib=WinspoolAdapter.CreateOpaquePrinterDib(source);
Check(dib.PixelFormat==PixelFormat.Format24bppRgb,"printer_dib_has_no_alpha_channel");Check(dib.Width==source.Width&&dib.Height==source.Height,"printer_dib_preserves_pixel_dimensions");
foreach(var point in new[]{new Point(0,0),new Point(79,0),new Point(0,59),new Point(79,59)}){var pixel=dib.GetPixel(point.X,point.Y);Check(pixel.R==255&&pixel.G==255&&pixel.B==255,$"transparent_corner_is_white_{point.X}_{point.Y}");}
var flattened=dib.GetPixel(40,30);Check(flattened.R is >=126 and <=129&&flattened.G is >=126 and <=129&&flattened.B is >=126 and <=129,"semi_transparent_pixel_is_flattened_over_white");dib.SetPixel(10,10,Color.Black);var monochrome=WinspoolAdapter.CreateMonochromePrinterDib(dib);Check(monochrome.Stride%4==0,"monochrome_stride_is_dword_aligned");Check(monochrome.IsWhite(0,0),"monochrome_white_pixel_is_palette_white");Check(!monochrome.IsWhite(10,10),"monochrome_black_pixel_is_palette_black");
Check(ReceiptRenderer.LogicalAlignmentForRtl(StringAlignment.Far)==StringAlignment.Near,"rtl_visual_right_uses_logical_near");Check(ReceiptRenderer.LogicalAlignmentForRtl(StringAlignment.Near)==StringAlignment.Far,"rtl_visual_left_uses_logical_far");

const string payload="""
{"schema":"sokna-print-document-v2","document_kind":"customer","title":"کافه سکنا","invoice_number":"آزمایشی","table_name":"میز ۲","display_date":"۱۴۰۵/۰۶/۱۶","total":520000,"currency":"تومان","settlement_label":"صندوق","sections":[{"title":"اقلام","items":[{"name":"اسپرسو","quantity":2,"unit_price":260000,"line_total":520000}]}],"template":{"base_font_size":23,"title_font_size":30,"table_font_size":28,"line_spacing":5,"margin":9,"show_time":true,"show_prices":true,"footer":"سپاس","design":{"density":"compact","header_alignment":"right","separator_style":"solid","item_layout":"columnar","section_order":["brand","meta","items","summary","settlement","footer"]}}}
""";
using var receipt=ReceiptRenderer.Render(payload,72,80,300,300);Check(receipt.Width==(int)Math.Round(72/25.4*300),"renderer_uses_exact_native_queue_width");Check(ReceiptRenderer.UsesBundledFont,"renderer_uses_bundled_font");Check(ReceiptRenderer.ActiveFontFamily.Equals("Vazirmatn",StringComparison.OrdinalIgnoreCase),"renderer_font_is_vazirmatn");
var grayscalePixels=0;for(var y=0;y<receipt.Height;y++)for(var x=0;x<receipt.Width;x++){var pixel=receipt.GetPixel(x,y);if(pixel.R is not (0 or 255)||pixel.G is not (0 or 255)||pixel.B is not (0 or 255))grayscalePixels++;}Check(grayscalePixels==0,"thermal_receipt_contains_no_grayscale_pixels");

const string longNamePayload="""
{"schema":"sokna-print-document-v2","document_kind":"customer","title":"کافه سکنا","total":999999999,"currency":"تومان","sections":[{"items":[{"name":"یک نام کالای بسیار بسیار طولانی برای آزمون چندخطی که نباید در ستون شرح بریده یا با سطر بعد هم‌پوشانی پیدا کند","quantity":123,"unit_price":987654321,"line_total":999999999}]}],"template":{"base_font_size":23,"title_font_size":30,"table_font_size":34,"line_spacing":5,"margin":9,"show_prices":true,"design":{"density":"compact","header_alignment":"center","separator_style":"solid","item_layout":"columnar","section_order":["brand","items","summary"]}}}
""";
using var long80=ReceiptRenderer.Render(longNamePayload,72,80,203,203);using var long58=ReceiptRenderer.Render(longNamePayload,50,58,203,203);Check(long80.Height>receipt.Height/3,"long_column_item_allocates_multiline_height");Check(long58.Height>100,"long_58mm_item_renders_without_zero_height_or_clip");

var hugeItems=string.Join(',',Enumerable.Range(0,1400).Select(i=>$"{{\"name\":\"آیتم بسیار بلند شماره {i} برای آزمون سقف ایمن سند و جلوگیری از بریدگی خاموش\",\"quantity\":1,\"unit_price\":1,\"line_total\":1}}"));var hugePayload=$"{{\"schema\":\"sokna-print-document-v2\",\"document_kind\":\"customer\",\"sections\":[{{\"items\":[{hugeItems}]}}],\"template\":{{\"table_font_size\":28,\"show_prices\":true,\"design\":{{\"item_layout\":\"two-line\",\"section_order\":[\"items\"]}}}}}}";
await ExpectThrowsAsync(()=>Task.Run(()=>{using var _=ReceiptRenderer.Render(hugePayload,50,58,203,203);}),"overlong_receipt_fails_before_silent_bitmap_clipping");

var physicalQueue=new PrinterQueueHealth("Physical",false,false,false,false,0,"Driver","USB001");
var mergedQueues=VirtualPrinterQueues.Merge(new[]{physicalQueue,VirtualPrinterQueues.PdfTestHealth()});
Check(mergedQueues.Count(x=>VirtualPrinterQueues.IsPdfTestQueue(x.Name))==1,"virtual_pdf_queue_is_deduplicated");
Check(mergedQueues.Any(x=>x.Name=="Physical"),"virtual_queue_merge_preserves_physical_queue");
var productionQueues=VirtualPrinterQueues.ForDiscovery(new[]{physicalQueue,VirtualPrinterQueues.PdfTestHealth()},false);
Check(productionQueues.Count==1&&productionQueues[0].Name=="Physical","pdf_test_queue_is_hidden_when_mode_disabled");
var uatQueues=VirtualPrinterQueues.ForDiscovery(new[]{physicalQueue},true);
Check(uatQueues.Count(x=>VirtualPrinterQueues.IsPdfTestQueue(x.Name))==1,"pdf_test_queue_is_advertised_only_when_mode_enabled");
Check(new AgentOptions().PdfTestSinkEnabled==false,"pdf_test_mode_defaults_off");

var policyRoot=Path.Combine(Path.GetTempPath(),"sokna-pdf-policy-"+Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(policyRoot);
    var policyConfig=Path.Combine(policyRoot,"config.json");
    Check(!PdfTestModePolicy.IsEnabled(policyConfig),"missing_pdf_test_config_fails_closed");
    new AgentOptions{ServerBaseUrl="http://127.0.0.1",PdfTestSinkEnabled=false}.Save(policyConfig);
    Check(!PdfTestModePolicy.IsEnabled(policyConfig),"pdf_test_policy_reads_explicit_disabled");
    new AgentOptions{ServerBaseUrl="http://127.0.0.1",PdfTestSinkEnabled=true}.Save(policyConfig);
    Check(PdfTestModePolicy.IsEnabled(policyConfig),"pdf_test_policy_reads_explicit_enabled");
    await File.WriteAllTextAsync(policyConfig,"not-json");
    Check(!PdfTestModePolicy.IsEnabled(policyConfig),"invalid_pdf_test_config_fails_closed");
}
finally
{
    try{Directory.Delete(policyRoot,true);}catch{}
}

var pdfRoot=Path.Combine(Path.GetTempPath(),"sokna-pdf-test-"+Guid.NewGuid().ToString("N"));
try
{
    var work=Path.Combine(pdfRoot,"work");Directory.CreateDirectory(work);
    var resultPath=Path.Combine(work,"result-101-202.json");
    var fencePath=Path.Combine(work,"fence-101-202.dat");
    var input=new WorkerInput(
        101,
        202,
        "receipt-test",
        VirtualPrinterQueues.PdfTestQueueName,
        payload,
        CryptoUtil.Sha256Hex(payload),
        80,
        72,
        2,
        resultPath,
        fencePath,
        Path.Combine(work,"start-101-202.dat"));
    var pdfResult=await new PdfTestSinkAdapter().SubmitAsync(input,CancellationToken.None);
    var pdfName="Sokna-job-101-attempt-202.pdf";
    var pdfPath=Path.Combine(pdfRoot,"TestPrints",pdfName);
    Check(pdfResult.Status=="submitted","pdf_test_sink_reports_submitted");
    Check(pdfResult.SpoolerJobId=="pdf:"+pdfName,"pdf_test_sink_reports_stable_artifact_id");
    Check(File.Exists(fencePath),"pdf_test_sink_writes_submission_fence");
    Check(File.Exists(pdfPath),"pdf_test_sink_writes_pdf_under_programdata_sibling");
    if(File.Exists(pdfPath))
    {
        var pdfBytes=await File.ReadAllBytesAsync(pdfPath);
        Check(pdfBytes.Length>500,"pdf_test_sink_output_is_nontrivial");
        Check(Encoding.ASCII.GetString(pdfBytes,0,Math.Min(8,pdfBytes.Length)).StartsWith("%PDF-1.4",StringComparison.Ordinal),"pdf_test_sink_has_pdf_header");
        var pdfText=Encoding.ASCII.GetString(pdfBytes);
        Check(pdfText.Split("/Type /Page /Parent",StringSplitOptions.None).Length-1==2,"pdf_test_sink_copies_become_pdf_pages");
        Check(pdfText.Contains("/MediaBox [0 0 226.772",StringComparison.Ordinal),"pdf_test_sink_uses_80mm_page_width");
    }
}
finally
{
    try{Directory.Delete(pdfRoot,true);}catch{}
}

if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}Console.WriteLine("PASS Sokna.PrintAgent.Worker.Tests");return 0;
