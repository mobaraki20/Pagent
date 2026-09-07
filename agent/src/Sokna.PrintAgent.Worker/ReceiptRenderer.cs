using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;

namespace Sokna.PrintAgent.Worker;

internal static class ReceiptRenderer
{
    private const int BaselineDpi=203;
    private static readonly Lazy<FontResources> Fonts=new(LoadFonts);
    internal static string ActiveFontFamily=>Fonts.Value.Family.Name;
    internal static bool UsesBundledFont=>Fonts.Value.IsBundled;

    public static Bitmap Render(string payloadJson,double printableWidthMm,double paperWidthMm,int dpiX=BaselineDpi,int dpiY=BaselineDpi)
    {
        if(dpiX<=0||dpiY<=0)throw new ArgumentOutOfRangeException(nameof(dpiX),"Printer DPI must be positive.");
        using var doc=JsonDocument.Parse(payloadJson);var root=doc.RootElement;
        if(!string.Equals(Get(root,"schema","sokna-print-document-v2"),"sokna-print-document-v2",StringComparison.Ordinal))throw new InvalidDataException("Print document schema پشتیبانی نمی‌شود.");
        var template=root.TryGetProperty("template",out var t)&&t.ValueKind==JsonValueKind.Object?t:default;
        var design=template.ValueKind==JsonValueKind.Object&&template.TryGetProperty("design",out var d)&&d.ValueKind==JsonValueKind.Object?d:default;
        var scaleX=dpiX/(double)BaselineDpi;var scaleY=dpiY/(double)BaselineDpi;
        var width=Math.Max(32,(int)Math.Round(printableWidthMm/25.4*dpiX));
        var marginX=Math.Clamp((int)Math.Round(GetInt(template,"margin",9)*scaleX),2,Math.Max(2,width/4));
        var marginY=Math.Max(2,(int)Math.Round(GetInt(template,"margin",9)*scaleY));
        var maxHeight=Math.Clamp((int)Math.Round(16000*scaleY),16000,48000);
        using var staging=new Bitmap(width,maxHeight,System.Drawing.Imaging.PixelFormat.Format24bppRgb);staging.SetResolution(dpiX,dpiY);
        using var g=Graphics.FromImage(staging);g.Clear(Color.White);
        // Thermal heads are monochrome. Grayscale antialias pixels get dithered by the driver and
        // make glyphs look like a scaled photo; grid-fitted 1-bit text stays crisp.
        g.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;g.SmoothingMode=SmoothingMode.None;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;
        var canvas=new Canvas(g,width,marginX,marginY,template,design,paperWidthMm,scaleX,scaleY);canvas.Render(root);
        var finalHeight=Math.Clamp(canvas.Y+marginY,(int)Math.Round(160*scaleY),maxHeight);
        var output=new Bitmap(width,finalHeight,System.Drawing.Imaging.PixelFormat.Format24bppRgb);output.SetResolution(dpiX,dpiY);
        using(var outputGraphics=Graphics.FromImage(output)){outputGraphics.Clear(Color.White);outputGraphics.DrawImageUnscaled(staging,0,0);}return output;
    }

    private sealed class Canvas
    {
        private readonly Graphics _g;private readonly int _width,_marginX,_marginY,_base,_title,_table,_gap;private readonly JsonElement _template,_design;private readonly double _paperWidth,_scaleX,_scaleY;
        private readonly bool _showActor,_showTime,_showOrder,_showSectionTitles,_showPrices;private readonly string _layout,_separatorStyle,_headerAlignment,_density;private readonly Dictionary<string,string> _labels;private readonly List<string> _sectionOrder;
        public int Y{get;private set;}
        public Canvas(Graphics g,int width,int marginX,int marginY,JsonElement template,JsonElement design,double paperWidth,double scaleX,double scaleY)
        {
            _g=g;_width=width;_marginX=marginX;_marginY=marginY;_template=template;_design=design;_paperWidth=paperWidth;_scaleX=scaleX;_scaleY=scaleY;Y=marginY;
            _base=Math.Clamp(GetInt(template,"base_font_size",23),18,42);_title=Math.Clamp(GetInt(template,"title_font_size",30),22,60);_table=Math.Clamp(GetInt(template,"table_font_size",28),24,72);_gap=Py(Math.Clamp(GetInt(template,"line_spacing",5),2,20));
            _showActor=GetBool(template,"show_actor",false);_showTime=GetBool(template,"show_time",true);_showOrder=GetBool(template,"show_order_number",true);_showSectionTitles=GetBool(template,"show_section_titles",true);_showPrices=GetBool(template,"show_prices",true);
            _layout=Get(design,"item_layout",paperWidth<=60?"columnar-compact":"columnar");if(_paperWidth<=60&&_layout=="columnar")_layout="columnar-compact";
            _separatorStyle=Get(design,"separator_style","solid");_headerAlignment=Get(design,"header_alignment","center");_density=Get(design,"density","compact");_labels=ReadLabels(design);_sectionOrder=ReadStringArray(design,"section_order");
        }
        public void Render(JsonElement root)
        {
            var kind=Get(root,"document_kind",Get(root,"job_type","").StartsWith("prep",StringComparison.Ordinal)?"preparation":"customer");var prep=kind=="preparation"||Get(root,"job_type","").StartsWith("prep",StringComparison.Ordinal);
            Text(Get(root,"title","کافه سکنا"),_title,true,_headerAlignment=="right"?StringAlignment.Far:StringAlignment.Center,2);
            if(GetBool(root,"is_reprint",false))BoxText(Label("reprint","چاپ مجدد"),Math.Max(_title,32),true);
            var order=_sectionOrder.Count>0?_sectionOrder:(prep?new List<string>{"status","meta","items","notes","footer"}:new List<string>{"brand","meta","items","summary","settlement","footer"});
            foreach(var section in order)
            {
                switch(section)
                {
                    case "brand":break;
                    case "status" when prep:DrawStatus(root);break;
                    case "meta":DrawMeta(root,prep);break;
                    case "items":Rule();if(prep)DrawPreparationItems(root);else DrawCustomerItems(root);break;
                    case "notes" when prep:DrawCustomerNote(root);break;
                    case "summary" when !prep:Rule();DrawSummary(root);break;
                    case "settlement" when !prep:DrawSettlement(root);break;
                    case "footer":var footer=Get(_template,"footer",Get(root,"footer",""));if(footer.Length>0){Rule();Text(footer,Math.Max(16,_base-5),false,StringAlignment.Center,3);}break;
                }
            }
        }
        private void DrawStatus(JsonElement root)
        {
            var badge=Get(root,"badge",Label("ticket_title","فیش آماده‌سازی"));if(badge.Length>0)Text(badge,Math.Max(_base,27),true,StringAlignment.Center,1);
            var status=Get(root,"status_label","");if(status.Length>0)Text(status,Math.Max(18,_base-3),true,StringAlignment.Center,2);
        }
        private void DrawMeta(JsonElement root,bool prep)
        {
            var parts=new List<string>();var table=Get(root,"table_name","");if(table.Length>0)parts.Add(table);
            var number=prep?Get(root,"order_number",""):Get(root,"invoice_number",Get(root,"badge",""));if(number.Length>0&&(prep?_showOrder:true))parts.Add((prep?"سفارش ":"")+FaDigits(number));
            if(_showTime){var date=Get(root,"display_date",Get(root,"created_at",""));if(date.Length>0)parts.Add(FaDigits(date));}if(parts.Count>0)Text(string.Join(" · ",parts),Math.Max(15,_base-5),true,StringAlignment.Center,3);
            if(_showActor){var actor=Get(root,"actor_name","");if(actor.Length>0)Text("ثبت‌کننده: "+actor,Math.Max(13,_base-7),false,StringAlignment.Center,2);}
        }
        private void DrawPreparationItems(JsonElement root)
        {
            if(root.TryGetProperty("sections",out var sections)&&sections.ValueKind==JsonValueKind.Array)foreach(var section in sections.EnumerateArray())
            {
                var sectionTitle=Get(section,"title","");if(_showSectionTitles&&sectionTitle.Length>0)Text(sectionTitle,Math.Max(20,_base),true,StringAlignment.Center,2);
                if(!section.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array)continue;
                foreach(var item in items.EnumerateArray())
                {
                    var quantity=FaDigits(Get(item,"quantity","1"));var name=Get(item,"name","—");Text(quantity+" × "+name,Math.Max(28,_table),true,StringAlignment.Far,_density=="comfortable"?3:2);
                    if(item.TryGetProperty("previous_quantity",out var previous)&&previous.ValueKind!=JsonValueKind.Null){var oldQuantity=FaDigits(previous.ToString());BoxText($"{Label("adjustment","اصلاح")}: قبلی {oldQuantity} ← جدید {quantity}",Math.Max(17,_base-3),true);}
                    var mode=Get(item,"fulfillment_mode","");if(mode=="takeaway")BoxText(Label("takeaway","بیرون‌بر"),Math.Max(18,_base-2),true);
                    var note=Get(item,"note","");if(note.Length>0)BoxText(Label("note","یادداشت")+": "+note,Math.Max(18,_base-2),true);Hairline();
                }
            }
        }
        private void DrawCustomerNote(JsonElement root){var note=Get(root,"customer_note","");if(note.Length>0)BoxText(Label("note","یادداشت")+": "+note,Math.Max(19,_base-1),true);}
        private void DrawCustomerItems(JsonElement root)
        {
            var items=new List<JsonElement>();if(root.TryGetProperty("sections",out var sections)&&sections.ValueKind==JsonValueKind.Array)foreach(var section in sections.EnumerateArray())if(section.TryGetProperty("items",out var array)&&array.ValueKind==JsonValueKind.Array)items.AddRange(array.EnumerateArray());
            var layout=_layout=="responsive-receipt"?(_paperWidth<=60?"two-line":"columnar"):_layout;if(!_showPrices)layout="two-line";if(layout is "columnar" or "columnar-compact")DrawCustomerColumns(items,layout=="columnar");else foreach(var item in items)DrawCustomerTwoLine(item);
        }
        private void DrawSummary(JsonElement root)
        {
            var discount=GetLong(root,"discount",0);var subtotal=GetLong(root,"subtotal",0);var total=GetLong(root,"total",0);var currency=Get(root,"currency","تومان");
            if(discount>0){Pair(Label("subtotal","جمع اقلام"),Money(subtotal)+" "+currency,Math.Max(17,_base-3),false);Pair(Label("discount","تخفیف"),"− "+Money(discount)+" "+currency,Math.Max(17,_base-3),false);}Pair(Label("total","جمع نهایی"),Money(total)+" "+currency,Math.Max(24,_base+2),true);
        }
        private void DrawSettlement(JsonElement root){var settlement=Get(root,"settlement_label","");if(settlement.Length>0)Text(Label("settlement","نحوه ثبت")+": "+settlement,Math.Max(15,_base-5),true,StringAlignment.Center,2);}
        private void DrawCustomerColumns(List<JsonElement> items,bool full)
        {
            var usable=_width-_marginX*2;var totalWidth=(int)(usable*.23);var quantityWidth=full?(int)(usable*.12):(int)(usable*.28);var unitWidth=full?(int)(usable*.22):0;var nameWidth=usable-totalWidth-quantityWidth-unitWidth;
            using var headerFont=MakeFont(Math.Max(13,_base-7),true);var headerY=Y;DrawCell("شرح",_marginX+totalWidth+quantityWidth+unitWidth,headerY,nameWidth,headerFont,StringAlignment.Far);
            if(full){DrawCell("فی",_marginX+totalWidth,headerY,unitWidth,headerFont,StringAlignment.Center);DrawCell("تعداد",_marginX+totalWidth+unitWidth,headerY,quantityWidth,headerFont,StringAlignment.Center);}else DrawCell("تعداد × فی",_marginX+totalWidth,headerY,quantityWidth,headerFont,StringAlignment.Center);
            DrawCell("مبلغ",_marginX,headerY,totalWidth,headerFont,StringAlignment.Near);Y+=Math.Max(Py(28),(int)headerFont.GetHeight(_g)+Py(8));Hairline();
            foreach(var item in items)
            {
                var name=Get(item,"name","—");var quantity=FaDigits(Get(item,"quantity","1"));var unit=Money(GetLong(item,"unit_price",0));var line=Money(GetLong(item,"line_total",0));using var font=MakeFont(Math.Max(15,_base-4),false);using var boldFont=MakeFont(Math.Max(15,_base-4),true);
                var nameRectangle=new RectangleF(_marginX+totalWidth+quantityWidth+unitWidth,Y,nameWidth,Py(500));var nameHeight=(int)Math.Ceiling(_g.MeasureString(name,font,nameRectangle.Size,Rtl(StringAlignment.Far)).Height)+Py(8);var rowHeight=Math.Max(Py(_density=="comfortable"?46:34),nameHeight);
                DrawCell(name,(int)nameRectangle.X,Y,nameWidth,font,StringAlignment.Far,rowHeight);if(full){DrawCell(unit,_marginX+totalWidth,Y,unitWidth,font,StringAlignment.Center,rowHeight);DrawCell(quantity,_marginX+totalWidth+unitWidth,Y,quantityWidth,font,StringAlignment.Center,rowHeight);}else DrawCell(quantity+" × "+unit,_marginX+totalWidth,Y,quantityWidth,font,StringAlignment.Center,rowHeight);DrawCell(line,_marginX,Y,totalWidth,boldFont,StringAlignment.Near,rowHeight);Y+=rowHeight;Hairline();
            }
        }
        private void DrawCustomerTwoLine(JsonElement item)
        {
            var name=Get(item,"name","—");var quantity=FaDigits(Get(item,"quantity","1"));var unit=Money(GetLong(item,"unit_price",0));var line=Money(GetLong(item,"line_total",0));
            if(_showPrices){var lineWidth=(int)((_width-_marginX*2)*.32);using var boldFont=MakeFont(Math.Max(17,_base-2),true);using var nameFont=MakeFont(Math.Max(18,_base-1),true);var nameWidth=_width-_marginX*2-lineWidth;var height=Math.Max(Py(_density=="comfortable"?48:38),(int)Math.Ceiling(_g.MeasureString(name,nameFont,new SizeF(nameWidth,Py(500)),Rtl(StringAlignment.Far)).Height)+Py(8));DrawCell(name,_marginX+lineWidth,Y,nameWidth,nameFont,StringAlignment.Far,height);DrawCell(line,_marginX,Y,lineWidth,boldFont,StringAlignment.Near,height);Y+=height;Text(quantity+" × "+unit,Math.Max(14,_base-6),false,StringAlignment.Far,1);}else Text(quantity+" × "+name,Math.Max(19,_base-1),true,StringAlignment.Far,2);
            var note=Get(item,"note","");if(note.Length>0)BoxText(Label("note","یادداشت")+": "+note,Math.Max(16,_base-4),true);Hairline();
        }
        private void Pair(string right,string left,int size,bool bold){using var font=MakeFont(size,bold);var height=Math.Max(Py(34),(int)font.GetHeight(_g)+Py(12));var half=(_width-_marginX*2)/2;DrawCell(right,_marginX+half,Y,half,font,StringAlignment.Far,height);DrawCell(left,_marginX,Y,half,font,StringAlignment.Near,height);Y+=height+_gap;}
        private void Text(string text,int size,bool bold,StringAlignment alignment,int gapMultiplier)
        {
            if(string.IsNullOrWhiteSpace(text))return;using var font=MakeFont(size,bold);using var format=Rtl(alignment);var rectangle=new RectangleF(_marginX,Y,_width-_marginX*2,Py(2000));var measured=_g.MeasureString(text,font,rectangle.Size,format);var height=Math.Max((int)font.GetHeight(_g)+Py(5),(int)Math.Ceiling(measured.Height)+Py(4));_g.DrawString(text,font,Brushes.Black,new RectangleF(_marginX,Y,_width-_marginX*2,height),format);Y+=height+_gap*gapMultiplier;
        }
        private void BoxText(string text,int size,bool bold)
        {
            using var font=MakeFont(size,bold);using var format=Rtl(StringAlignment.Center);var inset=Px(6);var inner=_width-_marginX*2-inset*2;var height=Math.Max(Py(38),(int)Math.Ceiling(_g.MeasureString(text,font,new SizeF(inner,Py(1000)),format).Height)+Py(12));var rectangle=new Rectangle(_marginX,Y,_width-_marginX*2,height);using var pen=new Pen(Color.Black,Math.Max(1,Py(2)));_g.DrawRectangle(pen,rectangle);_g.DrawString(text,font,Brushes.Black,new RectangleF(rectangle.Left+inset,rectangle.Top+Py(4),rectangle.Width-inset*2,rectangle.Height-Py(8)),format);Y+=height+_gap*2;
        }
        private void DrawCell(string text,int x,int y,int width,Font font,StringAlignment alignment,int height=0){using var format=Rtl(alignment);_g.DrawString(text,font,Brushes.Black,new RectangleF(x,y,width,height>0?height:Py(34)),format);}
        private void Rule(){Y+=_gap;using var pen=new Pen(Color.Black,Math.Max(1,Py(_separatorStyle=="minimal"?1:2)));if(_separatorStyle=="dashed")pen.DashStyle=DashStyle.Dash;_g.DrawLine(pen,_marginX,Y,_width-_marginX,Y);Y+=_gap*2;}
        private void Hairline(){using var pen=new Pen(Color.Black,Math.Max(1,Py(1))){DashStyle=DashStyle.Dot};_g.DrawLine(pen,_marginX,Y,_width-_marginX,Y);Y+=Math.Max(Py(2),_gap);}
        private Font MakeFont(int logicalSize,bool bold)=>ReceiptRenderer.Font((float)(logicalSize*_scaleY),bold);private int Px(int logicalPixels)=>Math.Max(1,(int)Math.Round(logicalPixels*_scaleX));private int Py(int logicalPixels)=>Math.Max(1,(int)Math.Round(logicalPixels*_scaleY));private string Label(string key,string fallback)=>_labels.TryGetValue(key,out var value)&&value.Length>0?value:fallback;
    }

    private sealed record FontResources(FontFamily Family,PrivateFontCollection? Collection,bool IsBundled);
    private static FontResources LoadFonts()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"Fonts");var regular=Path.Combine(directory,"Vazirmatn-Regular.ttf");var bold=Path.Combine(directory,"Vazirmatn-Bold.ttf");
        if(File.Exists(regular)&&File.Exists(bold))try{var collection=new PrivateFontCollection();collection.AddFontFile(regular);collection.AddFontFile(bold);var family=collection.Families.FirstOrDefault(item=>item.Name.Equals("Vazirmatn",StringComparison.OrdinalIgnoreCase))??collection.Families.FirstOrDefault();if(family is not null)return new FontResources(family,collection,true);collection.Dispose();}catch{}
        try{using var installed=new InstalledFontCollection();var names=installed.Families.Select(item=>item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var preferred in new[]{"Vazirmatn","Tahoma","Segoe UI"})if(names.Contains(preferred))return new FontResources(new FontFamily(preferred),null,false);}catch{}
        return new FontResources(FontFamily.GenericSansSerif,null,false);
    }
    private static Font Font(float size,bool bold){var family=Fonts.Value.Family;var requested=bold?FontStyle.Bold:FontStyle.Regular;var style=family.IsStyleAvailable(requested)?requested:FontStyle.Regular;return new Font(family,Math.Max(1,size),style,GraphicsUnit.Pixel);}
    internal static StringAlignment LogicalAlignmentForRtl(StringAlignment visualAlignment)=>visualAlignment switch{StringAlignment.Near=>StringAlignment.Far,StringAlignment.Far=>StringAlignment.Near,_=>StringAlignment.Center};
    private static StringFormat Rtl(StringAlignment visualAlignment)=>new(){Alignment=LogicalAlignmentForRtl(visualAlignment),LineAlignment=StringAlignment.Near,FormatFlags=StringFormatFlags.DirectionRightToLeft|StringFormatFlags.LineLimit,Trimming=StringTrimming.Word};
    private static Dictionary<string,string> ReadLabels(JsonElement design){var map=new Dictionary<string,string>(StringComparer.Ordinal);if(design.ValueKind==JsonValueKind.Object&&design.TryGetProperty("labels",out var labels)&&labels.ValueKind==JsonValueKind.Object)foreach(var property in labels.EnumerateObject())if(property.Value.ValueKind==JsonValueKind.String)map[property.Name]=property.Value.GetString()??"";return map;}
    private static List<string> ReadStringArray(JsonElement element,string name){var values=new List<string>();if(element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var array)&&array.ValueKind==JsonValueKind.Array)foreach(var item in array.EnumerateArray())if(item.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(item.GetString()))values.Add(item.GetString()!);return values;}
    private static string Get(JsonElement element,string name,string fallback){if(element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)){if(value.ValueKind==JsonValueKind.String)return value.GetString()??fallback;if(value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)return value.ToString();}return fallback;}
    private static int GetInt(JsonElement element,string name,int fallback)=>int.TryParse(Get(element,name,""),out var value)?value:fallback;
    private static long GetLong(JsonElement element,string name,long fallback){if(element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)){if(value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var number))return number;if(long.TryParse(value.ToString(),out number))return number;}return fallback;}
    private static bool GetBool(JsonElement element,string name,bool fallback){if(element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)){if(value.ValueKind==JsonValueKind.True)return true;if(value.ValueKind==JsonValueKind.False)return false;if(bool.TryParse(value.ToString(),out var boolean))return boolean;}return fallback;}
    private static string Money(long value)=>FaDigits(value.ToString("N0",System.Globalization.CultureInfo.InvariantCulture)).Replace(",","٬",StringComparison.Ordinal);
    private static string FaDigits(string value)=>string.Concat(value.Select(character=>character is >= '0' and <= '9'?"۰۱۲۳۴۵۶۷۸۹"[character-'0']:character));
}
