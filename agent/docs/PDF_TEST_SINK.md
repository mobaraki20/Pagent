# Sokna PDF Test Sink

## هدف

`Sokna PDF Test (TEST ONLY, 203 DPI)` یک مقصد مجازی داخلی Agent برای تست مسیر واقعی چاپ بدون مصرف کاغذ است. این قابلیت **Microsoft Print to PDF** را automate نمی‌کند و به Save As dialog یا session کاربر وابسته نیست.

## حالت پیش‌فرض Production

PDF Test Sink به‌صورت پیش‌فرض **خاموش** است (`pdf_test_sink_enabled=false`). در این حالت:

- Queue مجازی در discovery/heartbeat advertise نمی‌شود.
- Web نباید آن را به‌عنوان مقصد قابل انتخاب جدید ببیند.
- اگر یک destination قدیمی هنوز نام Queue PDF را نگه داشته باشد، Worker قبل از Submission Fence آن Job را با `pdf_test_mode_disabled` رد می‌کند؛ بنابراین سفارش بی‌صدا به فایل تبدیل نمی‌شود.

فعال‌سازی فقط برای QA/UAT از Control Console و گزینه «حالت تست PDF E2E» انجام می‌شود. پس از پایان تست باید دوباره خاموش شود.

## مسیر واقعی Job در حالت UAT

وقتی حالت تست PDF عمداً فعال است، Job دقیقاً همان چرخه production را طی می‌کند:

`Web destination -> Claim -> Accept -> Start -> Agent Service -> Worker -> durable submission fence -> PDF artifact -> durable WorkerResult -> Report`

تنها تفاوت مرحله نهایی است: به‌جای ارسال raster به Windows Spooler، همان thermal raster در یک PDF ذخیره می‌شود.

## Queue قابل انتخاب در حالت UAT

نام Queue:

`Sokna PDF Test (TEST ONLY, 203 DPI)`

Driver گزارش‌شده در health/heartbeat:

`Sokna Internal PDF Test Sink`

Port گزارش‌شده:

`SOKNA-PDF`

این Queue توسط خود Agent ساخته می‌شود و Windows printer واقعی نیست؛ فقط وقتی Test/UAT Mode روشن باشد advertise می‌شود.

## محل فایل‌ها و Save As دستی

PDFها در مسیر زیر ذخیره می‌شوند:

`C:\ProgramData\Sokna\PrintAgent\TestPrints\`

نام فایل deterministic و بر پایه Job/Attempt است:

`Sokna-job-{server_job_id}-attempt-{attempt_id}.pdf`

Control Console در صفحه «مرکز تست» دو ابزار دارد:

- «پوشه PDFهای آزمایشی» برای باز کردن محل فایل‌ها.
- «ذخیره آخرین PDF تست…» برای کپی‌کردن آخرین PDF واقعی با Save As به مسیر دلخواه کاربر.

Save As یک PDF‌ساز دوم نیست؛ فقط همان artifact واقعی تولیدشده توسط مسیر E2E را کپی می‌کند تا یک renderer و یک منبع حقیقت باقی بماند.

## هندسه و Fidelity

- Render profile مجازی: 203 x 203 DPI
- `paper_width_mm` و `printable_width_mm` از همان destination واقعی دریافت می‌شوند.
- خروجی از همان `ReceiptRenderer` و monochrome conversion مسیر thermal printer ساخته می‌شود.
- تعداد `copies` به تعداد pageهای یکسان در PDF تبدیل می‌شود.
- PDF برای بررسی محتوا، RTL، فونت، شکست خطوط، template، routing، destination و lifecycle Job مناسب است.

## Safety / Idempotency

قبل از اولین write قابل مشاهده به PDF، Worker همان Durable Submission Fence مسیر چاپ فیزیکی را ثبت می‌کند. اگر بعد از fence و قبل از durable result فرایند قطع شود، نتیجه `unknown/recovery_hold` می‌شود و Agent به‌صورت خودکار همان Attempt را دوباره چاپ نمی‌کند.

PDF نهایی با temporary file و atomic move نوشته می‌شود. فایل موجود برای همان Attempt overwrite نمی‌شود.

خاموش‌کردن Test Mode fail-closed است: حتی اگر Web هنوز مقصد قدیمی PDF داشته باشد، Worker قبل از fence آن را رد می‌کند و چیزی در `TestPrints` ایجاد نمی‌شود.

## امنیت

PDFها زیر ProgramData محافظت‌شده Agent باقی می‌مانند و ACL آن برای standard users باز نمی‌شود. Support Bundle نیز PDFهای رسید را به‌صورت خودکار ضمیمه نمی‌کند تا داده عملیاتی مشتری ناخواسته export نشود.

## چرا از Microsoft Print to PDF داخل Service استفاده نمی‌کنیم؟

Agent Service باید unattended باشد. بازشدن Save As dialog از مسیر Service قابل اتکا نیست و می‌تواند Job را منتظر UI نگه دارد. به همین دلیل تولید PDF در Worker بدون Dialog انجام می‌شود و Save As فقط در Control Console کاربر برای export فایل آماده استفاده می‌شود.

## محدودیت

این قابلیت جای UAT پرینتر فیزیکی را نمی‌گیرد. موارد زیر همچنان باید روی MEVA TP-UN2 / printer واقعی بررسی شوند:

- Driver geometry و Printable Area واقعی
- کیفیت thermal head
- cutter
- سرعت چاپ
- USB/LAN connectivity
- Spooler/driver failure behavior
- paper-out/offline/error status

## UAT پیشنهادی

1. در Control Console «حالت تست PDF E2E» را روشن کنید.
2. پس از refresh، یک destination آزمایشی در Web بسازید و Queue آن را روی `Sokna PDF Test (TEST ONLY, 203 DPI)` قرار دهید.
3. Customer receipt و Preparation receipt را جداگانه route کنید.
4. از Web یک Test Print واقعی ایجاد کنید.
5. PDF ایجادشده را از Control Console باز یا با «ذخیره آخرین PDF تست…» export کنید.
6. Job/Attempt/Report را در Web و Agent diagnostics بررسی کنید.
7. destinationهای واقعی را دوباره به USB/LAN printer برگردانید و Test Mode PDF را خاموش کنید.
8. تأیید کنید Queue مجازی دیگر advertise نمی‌شود.
9. در پایان، همان template را روی USB و LAN printer فیزیکی تست کنید.
