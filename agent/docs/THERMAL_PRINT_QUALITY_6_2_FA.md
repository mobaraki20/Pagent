# کیفیت چاپ حرارتی — Sokna Print Agent 6.2

وضعیت: مبنای فنی Release Candidate؛ پذیرش فیزیکی همچنان نیازمند UAT روی پرینتر واقعی است.

## نتیجهٔ بررسی

مسیر پیش‌فرض 6.2 برای چاپ فارسی/RTL باید همان **native 1-bit raster via Winspool** باقی بماند. این انتخاب برای سخت‌افزار نامشخص و صف‌های Windows سازگارترین راه است و نسبت به برگشت به bitmap رنگی/خاکستری یا چاپ متن Native پرینتر ریسک بسیار کمتری دارد.

### سیر اصلاحات 6.1.1 تا 6.1.3

- **6.1.1**: تصویر قبل از Winspool روی پس‌زمینهٔ سفید 24-bpp flatten شد تا Alpha/Padding توسط بعضی Driverها به ناحیهٔ سیاه تبدیل نشود. اما تصویر هنوز با `StretchDIBits` از اندازهٔ رندر به اندازهٔ مقصد scale می‌شد.
- **6.1.2**: Renderer با DPI واقعی Queue (`LOGPIXELSX/Y`) رندر کرد و عرض bitmap از ابتدا برابر عرض device شد؛ scaling نرم‌افزاری حذف شد. فونت Vazirmatn نیز به‌صورت bundled وارد محصول شد.
- **6.1.3**: مرز Winspool به DIB تک‌بیتی واقعی تغییر کرد؛ bitmap فقط 0/1 است و با `SetDIBitsToDevice` در ابعاد 1:1 تحویل Device Context می‌شود. این کار Driver را از dithering تصویر 24-bpp و interpolation بی‌نیاز می‌کند.
- **6.2**: همین مسیر حفظ شده؛ preview دقیق از همان Renderer استفاده می‌کند، اندازه‌گیری/رسم متن برای نام‌های طولانی یکسان شده و رسید بیش‌ازحد بلند به‌جای بریدگی خاموش قبل از submission fail می‌شود.

## چرا ظاهر «عکس‌مانند» ایجاد می‌شود

سه علت اصلی در این معماری عبارت‌اند از:

1. **Grayscale/Anti-alias**: پیکسل‌های خاکستری روی هد حرارتی یا Driver به dithering تبدیل می‌شوند و دور حروف بافت عکس‌مانند می‌گیرد.
2. **Scaling**: رندر در 203 DPI و Stretch به رزولوشن دیگر باعث resampling ساقه و منحنی حروف می‌شود.
3. **Driver image processing**: ارسال bitmap رنگی/24-bpp به Driver ممکن است halftone، enhancement یا تبدیل تونال دیگری را فعال کند.

6.2 هر سه را تا جای ممکن از مسیر نرم‌افزاری حذف می‌کند:

- `TextRenderingHint.SingleBitPerPixelGridFit`
- `SmoothingMode.None`
- native Queue DPI
- bitmap با عرض device دقیق
- 1-bpp DIB
- `SetDIBitsToDevice` بدون Stretch

## تصمیم فنی 6.2

### مسیر پیش‌فرض

`ReceiptRenderer -> native-size binary bitmap -> 1-bpp DIB -> SetDIBitsToDevice -> Windows Spooler`

این مسیر نباید بدون شواهد UAT به AntiAlias/ClearType، `HALFTONE`، `StretchDIBits` یا bitmap خاکستری برگردد.

### چرا ESC/POS RAW پیش‌فرض نیست

ارسال مستقیم ESC/POS raster می‌تواند Driver Windows را کاملاً دور بزند و روی مدل شناخته‌شده نتیجهٔ بسیار خوبی بدهد؛ با این حال در نصب فعلی مدل/firmware/protocol قطعی نیست، برخی Queueها RAW passthrough متفاوت دارند و چاپ متن Native پرینتر برای فارسی/RTL و Vazirmatn قابل اتکا نیست. بنابراین RAW ESC/POS فقط بعداً به‌صورت adapter اختیاری و capability-gated قابل بررسی است، نه جایگزین عمومی 6.2.

اگر مدل واقعی پرینتر ESC/POS استاندارد و رزولوشن/عرض dots آن قطعی شد، آزمایش A/B زیر می‌تواند تصمیم نسخهٔ بعد را مشخص کند:

- A: Winspool native 1-bpp فعلی
- B: همان bitmap نهایی 1-bpp، بدون رندر دوباره، از مسیر RAW graphics command در حالت x1/y1

هدف مقایسه کیفیت و latency است؛ data/template/bitmap باید ثابت باشد.

## معیارهای خودکار 6.2

- عرض رندر دقیقاً از `printable_width_mm / 25.4 * queue_dpi` مشتق می‌شود.
- bitmap چاپی نباید scale شود.
- Renderer حرارتی نباید پیکسل خاکستری تولید کند.
- DIB خروجی 1-bpp و stride آن DWORD-aligned است.
- سفید/سیاه در palette به‌صورت صریح تعریف می‌شود.
- پیش‌نمایش دقیق و چاپ از همان Renderer استفاده می‌کنند؛ اختلاف Driver/هد/کاغذ خارج از برابری دیجیتال است.

## UAT کیفیت روی صندوق

برای هر Queue واقعی این موارد ثبت شود:

1. نام دقیق Printer/Driver و Port.
2. DPI گزارش‌شده توسط Queue و printable width.
3. یک چاپ آزمایشی customer و یک preparation با:
   - فارسی ریز و درشت؛
   - متن Bold؛
   - نام قلم طولانی؛
   - اعداد بزرگ؛
   - خطوط افقی 1px/2px؛
   - باکس و separator.
4. عکس واضح همان رسید در کنار preview دقیق.
5. بررسی دیداری:
   - دور حروف نقطه‌نقطه/خاکستری نباشد؛
   - ساقهٔ حروف دوبل یا محو نباشد؛
   - عرض/مرکزشدن درست باشد؛
   - متن طولانی بریده نشود؛
   - فاصله‌ها یکنواخت باشد.
6. در Printer Preferences هر گزینهٔ Image/Photo/Halftone/Smoothing/Enhancement ثبت شود. برای مسیر 1-bpp ترجیح این است که Driver هیچ halftone یا photo enhancement اضافه نکند.

## منابع فنی

- Microsoft `TextRenderingHint.SingleBitPerPixelGridFit`: glyph bitmap با hinting برای بهبود stems/curves.
  https://learn.microsoft.com/en-us/dotnet/api/system.drawing.text.textrenderinghint
- Microsoft `SetStretchBltMode`: حالت HALFTONE برای بازنمونه‌برداری تصویر است؛ مسیر 6.2 اصولاً Stretch انجام نمی‌دهد.
  https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setstretchbltmode
- Epson ESC/POS raster graphics: raster در حالت normal با مقیاس x1/y1 به dots ارسال می‌شود؛ فرمان `GS v 0` در مستندات جدید obsolete است و Graphics Functionهای جدید ترجیح داده می‌شوند.
  https://download4.epson.biz/sec_pubs/pos/reference_en/escpos/gs_lv_0.html

## جمع‌بندی

برای Release Candidate 6.2 تغییر معماری دیگری در مسیر چاپ انجام نشود. مسیر فعلی از نظر نرم‌افزاری بهترین حالت عمومی برای جلوگیری از ظاهر عکس‌مانند است. تصمیم دربارهٔ RAW ESC/POS فقط پس از دریافت مدل/Driver واقعی و مقایسهٔ A/B روی همان bitmap اتخاذ شود.
