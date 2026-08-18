# معماری Sokna Print Agent 6 / Print API v4

## هدف و مقیاس
این معماری برای مقیاس واقعی سکنا طراحی شده است: حدود ۱۰ تا ۱۵ درخواست چاپ در دقیقه در پیک، با تست حاشیه اطمینان ۲۵ درخواست در دقیقه. هدف Throughput سازمانی نیست؛ هدف سه تضمین عملیاتی است:

1. **هیچ Print Intent الزامی پس از ثبت موفق سفارش بدون رکورد پایدار MySQL باقی نماند.**
2. **هیچ Duplicate خودکاری پس از ورود به محدوده مبهم Spooler ایجاد نشود.**
3. **هر شکست یا Retry ایمن باشد یا به Exception واضح و قابل Resolution تبدیل شود.**

تضمین «Exactly-once physical printing» مطرح نمی‌شود؛ Printer/Windows Spooler در این معماری تأیید سخت‌افزاری قابل اتکای خروج کاغذ ارائه نمی‌کنند.

## جریان اصلی
`Sokna PHP/MySQL → Print API v4 → .NET 10 Windows Service → SQLite WAL/FULL → Isolated PrintWorker → Winspool/GDI → Windows Spooler → Printer`

## Source of Truthها
- **MySQL**: Print Job، Required/Optional، Payload Snapshot، Hash، State و Server Attempt history.
- **SQLite در ProgramData**: مالکیت Durable یک Attempt پس از Claim، local receipt، snapshot مقصد، state recovery و Report Outbox.
- **Windows Spooler**: فقط پس از `StartDoc` مرجع پذیرش spooler job است؛ نه مرجع چاپ فیزیکی.
- **Control App**: Source of Truth نیست؛ بستن یا Hang آن هیچ اثری بر Service ندارد.

## مسیر ثبت سفارش
برای فیش آماده‌سازی الزامی، ایجاد Order و `print_jobs` در **همان Transaction MySQL** انجام می‌شود. داخل Transaction هیچ تماس شبکه، Agent، Windows یا Printer وجود ندارد. اگر destination mapping ناقص باشد Job با `blocked` ایجاد می‌شود؛ اگر Agent/Printer آفلاین باشد Job همچنان ساخته می‌شود.

## Claim و مالکیت Agent
1. Service فقط Queueهای واقعاً ready را به `claim` اعلام می‌کند.
2. Server حداکثر batch کوچک را `reserved` می‌کند و Lease می‌دهد.
3. Agent Payload + Hash + Lease + Destination snapshot را در یک Transaction SQLite ذخیره می‌کند.
4. Hash Verify می‌شود.
5. Agent `accept` می‌زند؛ فقط پاسخ موفق، Attempt را `claimed` می‌کند.
6. `claimed` دیگر به Agent دیگری واگذار نمی‌شود.

## Start و Submission Fence
- Service برای Attempt محلی `claimed` از Server مجوز `start` می‌گیرد.
- فقط state سروری `claimed` یا replay واقعی `started` می‌تواند مجوز Start بدهد. `submitted/failed/unknown/recovery_hold/cancelled/expired` هرگز دوباره چاپ را مجاز نمی‌کنند.
- Service Worker را با مسیر ثابت اجرا می‌کند و فوراً آن را به Windows Job Object با `KILL_ON_JOB_CLOSE` متصل می‌کند.
- Worker تا دریافت Start Signal از Service اجازه نزدیک‌شدن به Winspool ندارد.
- **خود Worker پس از Render و بررسی DPI/Printable Area، دقیقاً بلافاصله پیش از `StartDoc` در Winspool، Submission Fence را به‌صورت Durable می‌نویسد.**
- اگر Worker/Service پیش از Fence Crash کند Retry ایمن است. اگر Fence وجود داشته باشد و نتیجه اثبات‌پذیر نباشد `recovery_hold/unknown` ایجاد می‌شود و Retry خودکار ممنوع است.

این ترتیب عمداً از Fence خیلی زودهنگام جلوگیری می‌کند؛ Fence فقط زمانی نوشته می‌شود که Worker واقعاً در آستانه ورود به Spooler است.

## Durable Worker Result
Worker نتیجه را شامل `server_job_id`, `attempt_id`, `local_receipt_id`, `content_sha256`, state و `spooler_job_id` به فایل Durable/Atomic می‌نویسد. پس از Restart، Service ابتدا این نتیجه را اعتبارسنجی می‌کند. اگر Result معتبر موجود باشد همان Evidence گزارش می‌شود و **هیچ چاپ مجددی انجام نمی‌شود**.

## SQLite
مسیر استاندارد: `%ProgramData%\Sokna\PrintAgent`

- `journal_mode=WAL`
- `synchronous=FULL` روی هر Connection
- `foreign_keys=ON`
- `busy_timeout=5000`
- Local primary identity = `attempt_id`، نه `server_job_id`؛ بنابراین Retry ایمن همان Server Job می‌تواند Attempt جدید مستقل داشته باشد.
- Report Outbox Attempt-scoped است.
- Secret با DPAPI `LocalMachine` محافظت می‌شود.

## PrintWorker isolation
PrintWorker Process مستقل است. Service UI/GDI را اجرا نمی‌کند. Worker به Job Object وصل می‌شود تا Crash Service Worker یتیم باقی نگذارد. Timeout Worker:
- قبل از Fence → safe failure / Retry ممکن.
- بعد از Fence → recovery hold / Retry ممنوع.

## Rendering
Renderer Payload snapshot را deterministic رندر می‌کند و به Profile فونت کاربر وابسته نیست. فونت Machine-wide به ترتیب `Vazirmatn → Tahoma → Segoe UI` انتخاب می‌شود. RTL، ارقام فارسی، 58/80mm، wrap، ستون قیمت، preparation quantity-first، note/adjustment/takeaway و نشان بزرگ «چاپ مجدد» پشتیبانی می‌شوند.

WinspoolAdapter عرض چاپ را از `printable_width_mm` و DPI واقعی Queue محاسبه می‌کند و اگر عرض مقصد از printable area دستگاه بزرگ‌تر باشد **پیش از StartDoc** Fail می‌کند. Driver scaling/fit-to-page از طرف Agent درخواست نمی‌شود؛ UAT فیزیکی همچنان Gate است.

## Failover
- v4 فقط destinationهای Primary همان Agent را Claim می‌کند.
- Heartbeat offline به‌تنهایی Failover نمی‌سازد.
- `claimed`, `started`, `submitted`, `unknown`, `recovery_hold` هرگز Auto-failover ندارند.
- تغییر مسیر فقط برای Job اثباتاً چاپ‌نشده و با Audit انجام می‌شود.
- مهاجرت Legacy→v4 destination-by-destination است. وقتی Primary مقصد Protocol 4 شد، Endpoint Legacy اجازه رقابت برای همان مقصد را نمی‌دهد.

## Recovery principle
هر موقعیت فقط یکی از سه نتیجه دارد:
- **Safe-to-retry**: اثبات شده هنوز Spooler boundary طی نشده است.
- **Evidence of submitted**: Spooler Job ID ثبت شده؛ report ممکن است آفلاین retry شود ولی چاپ تکرار نمی‌شود.
- **Ambiguous**: `unknown/recovery_hold` و تصمیم انسانی.
