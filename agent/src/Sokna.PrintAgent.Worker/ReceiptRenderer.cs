using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;
using Sokna.PrintAgent.Core;
namespace Sokna.PrintAgent.Worker;

internal static class ReceiptRenderer
{
    private const int Dpi=203;
    private static readonly string FontFamily=ResolveFont();

    public static Bitmap Render(string payloadJson,double printableWidthMm,double paperWidthMm)
    {
        using var doc=JsonDocument.Parse(payloadJson);var root=doc.RootElement;
        if(!string.Equals(Get(root,"schema","sokna-print-document-v2"),"sokna-print-document-v2",StringComparison.Ordinal))throw new InvalidDataException("Print document schema پشتیبانی نمی‌شود.");
        var template=root.TryGetProperty("template",out var t)&&t.ValueKind==JsonValueKind.Object?t:default;
        var design=template.ValueKind==JsonValueKind.Object&&template.TryGetProperty("design",out var d)&&d.ValueKind==JsonValueKind.Object?d:default;
        var width=Math.Max(280,(int)Math.Round(printableWidthMm/25.4*Dpi));var margin=Math.Clamp(GetInt(template,"margin",9),4,40);var maxHeight=16000;
        using var staging=new Bitmap(width,maxHeight,System.Drawing.Imaging.PixelFormat.Format32bppArgb);staging.SetResolution(Dpi,Dpi);
        using var g=Graphics.FromImage(staging);g.Clear(Color.White);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.SmoothingMode=SmoothingMode.HighQuality;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        var c=new Canvas(g,width,margin,template,design,paperWidthMm);c.Render(root);var finalHeight=Math.Clamp(c.Y+margin,160,maxHeight);
        var output=new Bitmap(width,finalHeight,System.Drawing.Imaging.PixelFormat.Format32bppArgb);output.SetResolution(Dpi,Dpi);using(var og=Graphics.FromImage(output)){og.Clear(Color.White);og.DrawImageUnscaled(staging,0,0);}return output;
    }

    private sealed class Canvas
    {
        private readonly Graphics _g;private readonly int _width,_margin;private readonly JsonElement _template,_design;private readonly double _paperWidth;
        private readonly int _base,_title,_table,_gap;private readonly bool _showActor,_showTime,_showOrder,_showSectionTitles,_showPrices;private readonly string _layout;private readonly Dictionary<string,string> _labels;
        public int Y{get;private set;}
        public Canvas(Graphics g,int width,int margin,JsonElement template,JsonElement design,double paperWidth)
        {
            _g=g;_width=width;_margin=margin;_template=template;_design=design;_paperWidth=paperWidth;Y=margin;
            _base=Math.Clamp(GetInt(template,"base_font_size",23),18,42);_title=Math.Clamp(GetInt(template,"title_font_size",30),22,60);_table=Math.Clamp(GetInt(template,"table_font_size",28),24,72);_gap=Math.Clamp(GetInt(template,"line_spacing",5),2,20);
            _showActor=GetBool(template,"show_actor",false);_showTime=GetBool(template,"show_time",true);_showOrder=GetBool(template,"show_order_number",true);_showSectionTitles=GetBool(template,"show_section_titles",true);_showPrices=GetBool(template,"show_prices",true);
            _layout=Get(design,"item_layout",paperWidth<=60?"columnar-compact":"columnar");if(_paperWidth<=60&&_layout=="columnar")_layout="columnar-compact";
            _labels=ReadLabels(design);
        }

        public void Render(JsonElement root)
        {
            var kind=Get(root,"document_kind",Get(root,"job_type","").StartsWith("prep",StringComparison.Ordinal)?"preparation":"customer");var prep=kind=="preparation"||Get(root,"job_type","").StartsWith("prep",StringComparison.Ordinal);
            Text(Get(root,"title","کافه سکنا"),_title,true,StringAlignment.Center,2);
            var reprint=GetBool(root,"is_reprint",false);if(reprint)BoxText(Label("reprint","چاپ مجدد"),Math.Max(_title,32),true);
            var badge=Get(root,"badge","");if(badge.Length>0)Text(badge,Math.Max(_base,prep?27:20),true,StringAlignment.Center,2);
            DrawMeta(root,prep);Rule();
            if(prep)DrawPreparation(root);else DrawCustomer(root);
            var footer=Get(_template,"footer",Get(root,"footer",""));if(footer.Length>0){Rule();Text(footer,Math.Max(16,_base-5),false,StringAlignment.Center,3);}
        }

        private void DrawMeta(JsonElement root,bool prep)
        {
            var parts=new List<string>();var table=Get(root,"table_name","");if(table.Length>0)parts.Add(table);
            var number=prep?Get(root,"order_number",""):Get(root,"invoice_number","");if(number.Length>0&&(prep?_showOrder:true))parts.Add((prep?"سفارش ":"")+FaDigits(number));
            if(_showTime){var date=Get(root,"display_date",Get(root,"created_at",""));if(date.Length>0)parts.Add(FaDigits(date));}
            if(parts.Count>0)Text(string.Join(" · ",parts),Math.Max(15,_base-5),true,StringAlignment.Center,3);
            if(_showActor){var actor=Get(root,"actor_name","");if(actor.Length>0)Text("ثبت‌کننده: "+actor,Math.Max(13,_base-7),false,StringAlignment.Center,2);}
        }

        private void DrawPreparation(JsonElement root)
        {
            if(root.TryGetProperty("sections",out var sections)&&sections.ValueKind==JsonValueKind.Array)
            foreach(var section in sections.EnumerateArray())
            {
                var st=Get(section,"title","");if(_showSectionTitles&&st.Length>0)Text(st,Math.Max(20,_base),true,StringAlignment.Center,2);
                if(!section.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array)continue;
                foreach(var item in items.EnumerateArray())
                {
                    var qty=FaDigits(Get(item,"quantity","1"));var name=Get(item,"name","—");Text(qty+" × "+name,Math.Max(28,_table),true,StringAlignment.Far,2);
                    if(item.TryGetProperty("previous_quantity",out var prev)&&prev.ValueKind!=JsonValueKind.Null){var p=FaDigits(prev.ToString());BoxText($"اصلاح: قبلی {p} ← جدید {qty}",Math.Max(17,_base-3),true);}
                    var mode=Get(item,"fulfillment_mode","");if(mode=="takeaway")BoxText(Label("takeaway","بیرون‌بر"),Math.Max(18,_base-2),true);
                    var note=Get(item,"note","");if(note.Length>0)BoxText(Label("note","یادداشت")+": "+note,Math.Max(18,_base-2),true);
                    Hairline();
                }
            }
            var customerNote=Get(root,"customer_note","");if(customerNote.Length>0){BoxText(Label("note","یادداشت")+": "+customerNote,Math.Max(19,_base-1),true);}
        }

        private void DrawCustomer(JsonElement root)
        {
            var items=new List<JsonElement>();if(root.TryGetProperty("sections",out var sections)&&sections.ValueKind==JsonValueKind.Array)foreach(var section in sections.EnumerateArray())if(section.TryGetProperty("items",out var arr)&&arr.ValueKind==JsonValueKind.Array)items.AddRange(arr.EnumerateArray());
            var layout=_layout=="responsive-receipt"?(_paperWidth<=60?"two-line":"columnar"):_layout;
            if(!_showPrices)layout="two-line";
            if(layout is "columnar" or "columnar-compact")DrawCustomerColumns(items,layout=="columnar");else foreach(var item in items)DrawCustomerTwoLine(item);
            var discount=GetLong(root,"discount",0);var subtotal=GetLong(root,"subtotal",0);var total=GetLong(root,"total",0);Rule();
            if(discount>0){Pair(Label("subtotal","جمع اقلام"),Money(subtotal),Math.Max(17,_base-3),false);Pair(Label("discount","تخفیف"),"− "+Money(discount),Math.Max(17,_base-3),false);}
            Pair(Label("total","جمع نهایی"),Money(total)+" "+Get(root,"currency","تومان"),Math.Max(24,_base+2),true);
            var settlement=Get(root,"settlement_label","");if(settlement.Length>0)Text(Label("settlement","نحوه ثبت")+": "+settlement,Math.Max(15,_base-5),false,StringAlignment.Far,2);
        }

        private void DrawCustomerColumns(List<JsonElement> items,bool full)
        {
            var usable=_width-_margin*2;var totalW=(int)(usable*.23);var qtyW=full?(int)(usable*.12):(int)(usable*.28);var unitW=full?(int)(usable*.22):0;var nameW=usable-totalW-qtyW-unitW;
            using var hf=Font(Math.Max(13,_base-7),true);var hy=Y;DrawCell("شرح",_margin+totalW+qtyW+unitW,hy,nameW,hf,StringAlignment.Far);if(full){DrawCell("فی",_margin+totalW,hy,unitW,hf,StringAlignment.Center);DrawCell("تعداد",_margin+totalW+unitW,hy,qtyW,hf,StringAlignment.Center);}else DrawCell("تعداد × فی",_margin+totalW,hy,qtyW,hf,StringAlignment.Center);DrawCell("مبلغ",_margin,hy,totalW,hf,StringAlignment.Near);Y+=Math.Max(28,(int)hf.GetHeight(_g)+8);Hairline();
            foreach(var item in items)
            {
                var name=Get(item,"name","—");var qty=FaDigits(Get(item,"quantity","1"));var unit=Money(GetLong(item,"unit_price",0));var line=Money(GetLong(item,"line_total",0));using var f=Font(Math.Max(15,_base-4),false);using var bf=Font(Math.Max(15,_base-4),true);
                var nameRect=new RectangleF(_margin+totalW+qtyW+unitW,Y,nameW,500);var nameH=(int)Math.Ceiling(_g.MeasureString(name,f,nameRect.Size,Rtl(StringAlignment.Far)).Height)+8;var rowH=Math.Max(34,nameH);
                DrawCell(name,(int)nameRect.X,Y,nameW,f,StringAlignment.Far,rowH);if(full){DrawCell(unit,_margin+totalW,Y,unitW,f,StringAlignment.Center,rowH);DrawCell(qty,_margin+totalW+unitW,Y,qtyW,f,StringAlignment.Center,rowH);}else DrawCell(qty+" × "+unit,_margin+totalW,Y,qtyW,f,StringAlignment.Center,rowH);DrawCell(line,_margin,Y,totalW,bf,StringAlignment.Near,rowH);Y+=rowH;Hairline();
            }
        }

        private void DrawCustomerTwoLine(JsonElement item)
        {
            var name=Get(item,"name","—");var qty=FaDigits(Get(item,"quantity","1"));var unit=Money(GetLong(item,"unit_price",0));var line=Money(GetLong(item,"line_total",0));
            if(_showPrices){var lineW=(int)((_width-_margin*2)*.32);using var bf=Font(Math.Max(17,_base-2),true);using var nf=Font(Math.Max(18,_base-1),true);var nameW=_width-_margin*2-lineW;var h=Math.Max(38,(int)Math.Ceiling(_g.MeasureString(name,nf,new SizeF(nameW,500),Rtl(StringAlignment.Far)).Height)+8);DrawCell(name,_margin+lineW,Y,nameW,nf,StringAlignment.Far,h);DrawCell(line,_margin,Y,lineW,bf,StringAlignment.Near,h);Y+=h;Text(qty+" × "+unit,Math.Max(14,_base-6),false,StringAlignment.Far,1);}else Text(qty+" × "+name,Math.Max(19,_base-1),true,StringAlignment.Far,2);
            var note=Get(item,"note","");if(note.Length>0)BoxText(Label("note","یادداشت")+": "+note,Math.Max(16,_base-4),true);Hairline();
        }

        private void Pair(string right,string left,int size,bool bold)
        {
            using var f=Font(size,bold);var h=Math.Max(34,(int)f.GetHeight(_g)+12);var half=(_width-_margin*2)/2;DrawCell(right,_margin+half,Y,half,f,StringAlignment.Far,h);DrawCell(left,_margin,Y,half,f,StringAlignment.Near,h);Y+=h+_gap;
        }
        private void Text(string text,int size,bool bold,StringAlignment align,int gapMultiplier)
        {
            if(string.IsNullOrWhiteSpace(text))return;using var f=Font(size,bold);using var sf=Rtl(align);var rect=new RectangleF(_margin,Y,_width-_margin*2,2000);var measured=_g.MeasureString(text,f,rect.Size,sf);var h=Math.Max((int)f.GetHeight(_g)+5,(int)Math.Ceiling(measured.Height)+4);_g.DrawString(text,f,Brushes.Black,new RectangleF(_margin,Y,_width-_margin*2,h),sf);Y+=h+_gap*gapMultiplier;
        }
        private void BoxText(string text,int size,bool bold)
        {
            using var f=Font(size,bold);using var sf=Rtl(StringAlignment.Center);var inner=_width-_margin*2-12;var h=Math.Max(38,(int)Math.Ceiling(_g.MeasureString(text,f,new SizeF(inner,1000),sf).Height)+12);var rect=new Rectangle(_margin,Y,_width-_margin*2,h);_g.DrawRectangle(new Pen(Color.Black,2),rect);_g.DrawString(text,f,Brushes.Black,new RectangleF(rect.Left+6,rect.Top+4,rect.Width-12,rect.Height-8),sf);Y+=h+_gap*2;
        }
        private void DrawCell(string text,int x,int y,int width,Font f,StringAlignment alignment,int height=34){using var sf=Rtl(alignment);_g.DrawString(text,f,Brushes.Black,new RectangleF(x,y,width,height),sf);}
        private void Rule(){Y+=_gap;var pen=new Pen(Color.Black,2);_g.DrawLine(pen,_margin,Y,_width-_margin,Y);Y+=_gap*2;pen.Dispose();}
        private void Hairline(){var pen=new Pen(Color.LightGray,1);_g.DrawLine(pen,_margin,Y,_width-_margin,Y);Y+=Math.Max(2,_gap);pen.Dispose();}
        private string Label(string key,string fallback)=>_labels.TryGetValue(key,out var v)&&v.Length>0?v:fallback;
    }

    private static Dictionary<string,string> ReadLabels(JsonElement design)
    {
        var map=new Dictionary<string,string>(StringComparer.Ordinal);if(design.ValueKind==JsonValueKind.Object&&design.TryGetProperty("labels",out var l)&&l.ValueKind==JsonValueKind.Object)foreach(var p in l.EnumerateObject())if(p.Value.ValueKind==JsonValueKind.String)map[p.Name]=p.Value.GetString()??"";return map;
    }
    private static Font Font(float size,bool bold)=>new(FontFamily,size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
    private static string ResolveFont(){try{using var fonts=new InstalledFontCollection();var names=fonts.Families.Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var preferred in new[]{"Vazirmatn","Tahoma","Segoe UI"})if(names.Contains(preferred))return preferred;}catch{}return "Tahoma";}
    private static StringFormat Rtl(StringAlignment a)=>new(){Alignment=a,LineAlignment=StringAlignment.Near,FormatFlags=StringFormatFlags.DirectionRightToLeft|StringFormatFlags.LineLimit,Trimming=StringTrimming.Word};
    private static string Get(JsonElement e,string name,string fallback){if(e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var x)){if(x.ValueKind==JsonValueKind.String)return x.GetString()??fallback;if(x.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)return x.ToString();}return fallback;}
    private static int GetInt(JsonElement e,string name,int fallback)=>int.TryParse(Get(e,name,""),out var v)?v:fallback;
    private static long GetLong(JsonElement e,string name,long fallback){if(e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var x)){if(x.ValueKind==JsonValueKind.Number&&x.TryGetInt64(out var n))return n;if(long.TryParse(x.ToString(),out n))return n;}return fallback;}
    private static bool GetBool(JsonElement e,string name,bool fallback){if(e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var x)){if(x.ValueKind==JsonValueKind.True)return true;if(x.ValueKind==JsonValueKind.False)return false;if(bool.TryParse(x.ToString(),out var b))return b;}return fallback;}
    private static string Money(long n)=>FaDigits(n.ToString("N0",System.Globalization.CultureInfo.InvariantCulture)).Replace(",","٬",StringComparison.Ordinal);
    private static string FaDigits(string s)=>string.Concat(s.Select(ch=>ch is >= '0' and <= '9'?"۰۱۲۳۴۵۶۷۸۹"[ch-'0']:ch));
}
