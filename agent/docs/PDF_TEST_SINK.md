# Sokna PDF Test Sink

## هدف

`Sokna PDF Test (TEST ONLY, 203 DPI)` یک مقصد مجازی داخلی Agent برای تست مسیر واقعی چاپ بدون مصرف کاغذ است. این قابلیت **Microsoft Print to PDF** را automate نمی‌کند و به Save As dialog یا session کاربر وابسته نیست.

## مسیر واقعی Job

Job دقیقاً همان چرخه production را طی می‌کند:

`Web destination -> Claim -> Accept -> Start -> Agent Service -> Worker -> durable submission fence -> PDF artifact -> durable WorkerResult -> Report`

تنها تفاوت مرحله نهایی است: به‌جای ارسال raster به Windows Spooler، همان thermal raster در یک PDF ذخیره می‌شود.

## Queue قابل انتخاب

نام Queue:

`Sokna PDF Test (TEST ONLY, 203 DPI)`

Driver گزارش‌شده در health/heartbeat:

`Sokna Internal PDF Test Sink`

Port گزارش‌شده:

`SOKNA-PDF`

این Queue توسط خود Agent advertise می‌شود و Windows printer واقعی نیست.

## محل فایل‌ها

PDFها در مسیر زیر ذخیره می‌شوند:

`C:\ProgramData\Sokna\PrintAgent\TestPrints\`

نام فایل deterministic و بر پایه Job/Attempt است:

`Sokna-job-{server_job_id}-attempt-{attempt_id}.pdf`

Control Console در صفحه «مرکز تست» دکمه «پوشه PDFهای آزمایشی» دارد.

## هندسه و Fidelity

- Render profile مجازی: 203 x 203 DPI
- `paper_width_mm` و `printable_width_mm` از همان destination واقعی دریافت می‌شوند.
- خروجی از همان `ReceiptRenderer` و monochrome conversion مسیر thermal printer ساخته می‌شود.
- تعداد `copies` به تعداد pageهای یکسان در PDF تبدیل می‌شود.
- PDF برای بررسی محتوا، RTL، فونت، شکست خطوط، template، routing، destination و lifecycle Job مناسب است.

## Safety / Idempotency

قبل از اولین write قابل مشاهده به PDF، Worker همان Durable Submission Fence مسیر چاپ فیزیکی را ثبت می‌کند. اگر بعد از fence و قبل از durable result فرایند قطع شود، نتیجه `unknown/recovery_hold` می‌شود و Agent به‌صورت خودکار همان Attempt را دوباره چاپ نمی‌کند.

PDF نهایی با temporary file و atomic move نوشته می‌شود. فایل موجود برای همان Attempt overwrite نمی‌شود.

## امنیت

PDFها زیر ProgramData محافظت‌شده Agent باقی می‌مانند و ACL آن برای standard users باز نمی‌شود. Support Bundle نیز PDFهای رسید را به‌صورت خودکار ضمیمه نمی‌کند تا داده عملیاتی مشتری ناخواسته export نشود.

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

1. یک destination آزمایشی در Web بسازید و Queue آن را روی `Sokna PDF Test (TEST ONLY, 203 DPI)` قرار دهید.
2. Customer receipt و Preparation receipt را جداگانه route کنید.
3. از Web یک Test Print واقعی ایجاد کنید.
4. PDF ایجادشده را از Control Console باز کنید.
5. Job/Attempt/Report را در Web و Agent diagnostics بررسی کنید.
6. پس از تأیید محتوا و routing، همان template را روی USB و LAN printer فیزیکی تست کنید.
