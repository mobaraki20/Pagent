using System.Drawing;
using System.Drawing.Imaging;
using Sokna.PrintAgent.Worker;

var failures=new List<string>();
void Check(bool value,string name){if(!value)failures.Add(name);}

using var source=new Bitmap(80,60,PixelFormat.Format32bppArgb);
source.SetPixel(40,30,Color.FromArgb(128,0,0,0));
using var dib=WinspoolAdapter.CreateOpaquePrinterDib(source);

Check(dib.PixelFormat==PixelFormat.Format24bppRgb,"printer_dib_has_no_alpha_channel");
Check(dib.Width==source.Width&&dib.Height==source.Height,"printer_dib_preserves_pixel_dimensions");
foreach(var point in new[]{new Point(0,0),new Point(79,0),new Point(0,59),new Point(79,59)})
{
    var pixel=dib.GetPixel(point.X,point.Y);
    Check(pixel.R==255&&pixel.G==255&&pixel.B==255,$"transparent_corner_is_white_{point.X}_{point.Y}");
}
var flattened=dib.GetPixel(40,30);
Check(flattened.R is >=126 and <=129&&flattened.G is >=126 and <=129&&flattened.B is >=126 and <=129,"semi_transparent_pixel_is_flattened_over_white");

Check(ReceiptRenderer.LogicalAlignmentForRtl(StringAlignment.Far)==StringAlignment.Near,"rtl_visual_right_uses_logical_near");
Check(ReceiptRenderer.LogicalAlignmentForRtl(StringAlignment.Near)==StringAlignment.Far,"rtl_visual_left_uses_logical_far");

const string payload="""
{"schema":"sokna-print-document-v2","document_kind":"customer","title":"کافه سکنا","invoice_number":"آزمایشی","table_name":"میز ۲","display_date":"۱۴۰۵/۰۶/۱۶","total":520000,"currency":"تومان","settlement_label":"صندوق","sections":[{"title":"اقلام","items":[{"name":"اسپرسو","quantity":2,"unit_price":260000,"line_total":520000}]}],"template":{"base_font_size":23,"title_font_size":30,"table_font_size":28,"line_spacing":5,"margin":9,"show_time":true,"show_prices":true,"footer":"سپاس","design":{"density":"compact","header_alignment":"right","separator_style":"solid","item_layout":"columnar","section_order":["brand","meta","items","summary","settlement","footer"]}}}
""";
using var receipt=ReceiptRenderer.Render(payload,72,80,300,300);
Check(receipt.Width==(int)Math.Round(72/25.4*300),"renderer_uses_exact_native_queue_width");
Check(ReceiptRenderer.UsesBundledFont,"renderer_uses_bundled_font");
Check(ReceiptRenderer.ActiveFontFamily.Equals("Vazirmatn",StringComparison.OrdinalIgnoreCase),"renderer_font_is_vazirmatn");
var grayscalePixels=0;
for(var y=0;y<receipt.Height;y++)for(var x=0;x<receipt.Width;x++)
{
    var pixel=receipt.GetPixel(x,y);
    if(pixel.R is not (0 or 255)||pixel.G is not (0 or 255)||pixel.B is not (0 or 255))grayscalePixels++;
}
Check(grayscalePixels==0,"thermal_receipt_contains_no_grayscale_pixels");

if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}
Console.WriteLine("PASS Sokna.PrintAgent.Worker.Tests");
return 0;
