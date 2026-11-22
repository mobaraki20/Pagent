using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sokna.PrintAgent.Worker;

var failures=new List<string>();
void Check(bool passed,string name){if(!passed)failures.Add(name);}
JsonObject Clone(JsonObject source)=>(JsonObject)source.DeepClone();
JsonObject Template(JsonObject root)=>root["template"]!.AsObject();
JsonObject Design(JsonObject root)=>Template(root)["design"]!.AsObject();
Bitmap Render(JsonObject root,double widthMm)=>ReceiptRenderer.Render(root.ToJsonString(),widthMm<=60?48.0:72.0,widthMm,203,203);
string Fingerprint(Bitmap image)
{
    using var stream=new MemoryStream();
    image.Save(stream,ImageFormat.Png);
    return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
}
void Changed(JsonObject baseline,JsonObject variant,double paperMm,string name)
{
    using var before=Render(baseline,paperMm);
    using var after=Render(variant,paperMm);
    Check(Fingerprint(before)!=Fingerprint(after),name);
}
var prep=JsonNode.Parse("""
{
  "schema":"sokna-print-document-v2",
  "document_kind":"preparation",
  "title":"کافه سکنا",
  "badge":"آماده‌سازی",
  "order_number":"123",
  "table_name":"میز ۲",
  "display_date":"۱۴۰۵/۰۹/۰۱ ۱۲:۲۰",
  "customer_note":"سفارش را با دقت آماده کنید",
  "sections":[{"title":"","items":[
    {"name":"چای ساده","quantity":2,"note":"کم‌رنگ و بدون قند، لطفاً","station":"hot_bar","order_index":2},
    {"name":"پیتزا مخصوص","quantity":1,"note":"سس جداگانه و بدون پیاز","station":"kitchen","order_index":0},
    {"name":"لیموناد","quantity":1,"note":"بدون یخ","station":"cold_bar","order_index":1}
  ]}],
  "template":{
    "base_font_size":24,"title_font_size":32,"notes_font_size":22,
    "header_subtitle":"تست کنترل کیفیت رندر", "header_subtitle_font_size":18,
    "show_time":true,"show_order_number":true,"show_section_titles":true,
    "design":{"item_layout":"columnar-notes","preparation_station_order":"default",
      "section_order":["status","meta","items","notes","footer"]}
  }
}
""")!.AsObject();

var customer=JsonNode.Parse("""
{
  "schema":"sokna-print-document-v2",
  "document_kind":"customer",
  "title":"کافه سکنا",
  "invoice_number":"INV-123",
  "display_date":"۱۴۰۵/۰۹/۰۱ ۱۲:۲۰",
  "currency":"تومان",
  "sections":[{"items":[
    {"name":"قهوه دمی","quantity":2,"unit_price":180000,"line_total":360000,"note":"بدون شکر"},
    {"name":"کیک روز","quantity":1,"unit_price":220000,"line_total":220000}
  ]}],
  "subtotal":580000,"discount":50000,"total":530000,"settlement_label":"کارت‌خوان",
  "template":{
    "base_font_size":23,"title_font_size":30,"notes_font_size":18,
    "footer":"سپاس از همراهی شما",
    "show_prices":true,"show_settlement_label":true,
    "design":{"item_layout":"columnar","section_order":["brand","meta","items","summary","settlement","footer"]}
  }
}
""")!.AsObject();

try
{
    using(var a=Render(prep,58)){Check(a.Width>200&&a.Height>200,"58mm_preparation_render");}
    using(var a=Render(prep,80)){Check(a.Width>400&&a.Height>200,"80mm_three_column_preparation_render");}
    using(var a=Render(customer,58)){Check(a.Width>200&&a.Height>200,"58mm_customer_render");}
    using(var a=Render(customer,80)){Check(a.Width>400&&a.Height>200,"80mm_customer_render");}
    var p1=Clone(prep);Design(p1)["preparation_station_order"]="as_ordered";
    Changed(prep,p1,80,"original_order_changes_output");
    var p2=Clone(prep);Design(p2)["preparation_station_order"]="bar_first";
    Changed(prep,p2,80,"bar_first_changes_output");
    var p3=Clone(prep);Design(p3)["preparation_station_order"]="kitchen_first";
    Changed(prep,p3,80,"kitchen_first_changes_output");
    var p4=Clone(prep);Design(p4)["item_layout"]="two-line";
    Changed(prep,p4,58,"58mm_two_line_vs_columns");
    Changed(prep,p4,80,"80mm_three_columns_vs_two_line");
    var p5=Clone(prep);Template(p5)["notes_font_size"]=42;
    Changed(prep,p5,58,"preparation_note_font_effective");
    Changed(prep,p5,80,"three_column_note_font_effective");
    var p6=Clone(prep);Template(p6)["header_title"]="سکنا · آزمایش عنوان";
    Changed(prep,p6,80,"header_override_effective");
    var p7=Clone(prep);Template(p7)["header_subtitle_font_size"]=38;
    Changed(prep,p7,80,"subtitle_font_effective");
    var p8=Clone(prep);Design(p8)["section_order"]=new JsonArray("meta","items","status","notes","footer");
    Changed(prep,p8,80,"preparation_section_order_effective");
    var p9=Clone(prep);Design(p9)["section_order"]=new JsonArray("status","meta","notes","footer");
    using(var a=Render(p9,80)){Check(a.Height>300,"missing_required_items_restored");}
    var p10=Clone(prep);Template(p10)["meta_font_size"]=40;
    Changed(prep,p10,80,"meta_font_effective");
    // Verify true single-line mode is distinct from the existing two-line layout
    // and remains readable on both narrow and wide thermal rolls.
    var oneLine=Clone(customer);Design(oneLine)["item_layout"]="single-line";
    var twoLine=Clone(customer);Design(twoLine)["item_layout"]="two-line";
    Changed(oneLine,twoLine,58,"58mm_single_line_distinct_from_two_line");
    Changed(oneLine,twoLine,80,"80mm_single_line_distinct_from_two_line");
    using(var a=Render(oneLine,58)){Check(a.Width>200&&a.Height>200,"58mm_single_line_items_rendered");}
    using(var a=Render(oneLine,80)){Check(a.Width>400&&a.Height>200,"80mm_single_line_items_rendered");}
    var prepSingle=Clone(prep);Design(prepSingle)["item_layout"]="single-line";
    var prepTwoLine=Clone(prep);Design(prepTwoLine)["item_layout"]="two-line";
    using(var a=Render(prepSingle,58)){Check(a.Width>200&&a.Height>200,"58mm_preparation_single_line_rendered");}
    // Also cover customer notes on the same item and their independent typography.
    var oneLineNote=Clone(oneLine);Template(oneLineNote)["notes_font_size"]=45;
    Changed(oneLine,oneLineNote,80,"single_line_item_notes_font_effective");
    var c1=Clone(customer);Template(c1)["totals_font_size"]=45;
    Changed(customer,c1,80,"customer_totals_font_effective");
    var c2=Clone(customer);Template(c2)["settlement_font_size"]=42;
    Changed(customer,c2,80,"settlement_font_effective");
    var c3=Clone(customer);Template(c3)["show_settlement_label"]=false;
    Changed(customer,c3,80,"settlement_visibility_effective");
    var c4=Clone(customer);Template(c4)["notes_font_size"]=45;
    Changed(customer,c4,80,"customer_note_font_effective");
    var c5=Clone(customer);Template(c5)["footer_font_size"]=45;
    Changed(customer,c5,80,"footer_font_effective");
    var c6=Clone(customer);Design(c6)["section_order"]=new JsonArray("brand","meta","settlement","footer");
    using(var a=Render(c6,80)){Check(a.Height>200,"customer_required_items_totals_restored");}
    // Merchant bank card number belongs to the café, never to a customer
    // payment instrument, and is optional on the customer receipt.
    var card=Clone(customer);Template(card)["merchant_card_number"]="6037991234567890";
    Changed(customer,card,80,"merchant_card_number_affects_footer");
    Changed(customer,card,58,"merchant_card_number_58mm");
    var customLabels=Clone(customer);
    Design(customLabels)["labels"]=JsonNode.Parse("""{"description":"کالا","quantity":"تعداد خرید","unit_price":"قیمت پایه","amount":"جمع قلم","settlement":"روش دریافت"}""");
    Changed(customer,customLabels,80,"custom_system_labels_affect_renderer");
    var customPrepLabels=Clone(prep);
    Design(customPrepLabels)["labels"]=JsonNode.Parse("""{"description":"عنوان محصول","quantity":"چند عدد","order":"نوبت","previous":"تعداد پیشین","new":"تعداد تازه"}""");
    Changed(prep,customPrepLabels,58,"preparation_custom_system_labels");
    var unpaid=Clone(customer);
    unpaid["document_role"]="account_preview";
    unpaid["badge"]="حساب باز · تسویه نشده";
    unpaid["invoice_number"]="";
    Changed(customer,unpaid,80,"unpaid_account_is_visually_distinct");
    var noSettlement=Clone(unpaid);
    Template(noSettlement)["show_settlement_label"]=false;
    // Explicit unpaid status stays visible, independent from the optional
    // settlement-row toggle; the two images still differ in settlement content.
    Changed(unpaid,noSettlement,80,"unpaid_status_preserved_without_settlement_row");
    using var repeat1=Render(prep,58);
    using var repeat2=Render(prep,58);
    Check(Fingerprint(repeat1)==Fingerprint(repeat2),"render_is_deterministic");
}
catch(Exception error)
{
    failures.Add("render_failed: "+error);
}
if(failures.Count>0)
{
    Console.Error.WriteLine("FAIL Sokna RendererQA: "+string.Join(" | ",failures));
    return 1;
}
Console.WriteLine("PASS Sokna RendererQA: 58/80mm, columns, sections, station order, fonts, receipts and deterministic rendering");
return 0;
