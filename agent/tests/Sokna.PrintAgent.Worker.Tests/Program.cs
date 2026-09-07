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

if(failures.Count>0){Console.Error.WriteLine("FAIL "+string.Join(",",failures));return 1;}
Console.WriteLine("PASS Sokna.PrintAgent.Worker.Tests");
return 0;
