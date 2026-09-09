using System.Drawing;
using System.Drawing.Imaging;
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

if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}Console.WriteLine("PASS Sokna.PrintAgent.Worker.Tests");return 0;
