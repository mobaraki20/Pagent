# عیب‌یابی Sokna Print Agent 6

## Setup.exe خطا می‌دهد
`Setup.exe` Failure را stage-based ثبت می‌کند. ابتدا Reference ID و Stage را از stderr یا پیام Setup بردارید و سپس فایل متناظر را در این مسیر بررسی کنید:

`%ProgramData%\Sokna\PrintAgentSetup\logs\setup-<reference-id>.json`

Log شامل stage، exit code، child process exit code، exception type و stdout/stderr پاک‌سازی‌شده است. Authorization/Bearer/Token/Secret/HMAC نباید در Log ثبت شود.

از 6.2.1، اگر Windows Service هنگام startup متوقف شود، Service تلاش می‌کند علت fatal را در مسیر زیر نیز ثبت کند و Installer همان علت پاک‌سازی‌شده را در خطای `service_start` نمایش می‌دهد:

`%ProgramData%\Sokna\PrintAgent\logs\startup-fatal.json`

Root Cause تاریخی `Setup.exe returned 1` در این پروژه `Get-FileHash` داخل Windows PowerShell child host بود. این مورد قبلاً رفع شده و SHA-256 validation با `System.Security.Cryptography.SHA256` انجام می‌شود. اگر همان Stage دوباره Fail شد، علت قبلی را حدس نزنید؛ Evidence همان Run را بررسی کنید.

## Access denied روی Desktop/Start Menu یا مسیر Program Files
نسخه 6.2.0 ACL پوشه برنامه را بیش از حد محدود می‌کرد و با حذف inheritance فقط `SYSTEM` و `Administrators` را نگه می‌داشت. نتیجه می‌توانست این باشد که Explorer غیر-elevated نتواند target/icon شورتکات را بخواند و کاربر مجبور شود به‌صورت دستی دسترسی پوشه را اصلاح کند.

از 6.2.1، ACL پوشه برنامه به‌صورت locale-neutral با SID تنظیم می‌شود:
- `SYSTEM`: Read & Execute
- `Administrators`: Full Control
- `Users`: Read & Execute

کاربر عادی حق Write/Modify روی binaryهای Program Files ندارد. داده‌های حساس و قابل‌نوشتن همچنان در ProgramData می‌مانند و ACL آن فقط برای `SYSTEM` و `Administrators` است.

Repair/upgrade همان نسخه نیز قبل از swap، ACL نصب قبلی را repair می‌کند؛ Control/Worker باقی‌مانده متوقف می‌شوند؛ Service تا رسیدن به `Stopped` بررسی می‌شود؛ و rename پوشه با retry محدود انجام می‌شود تا lock گذرای Explorer/AV بلافاصله نصب را خراب نکند.

## Service نصب شده ولی بالا نمی‌ماند
- `sc query SoknaPrintAgent6`
- `sc qc SoknaPrintAgent6`
- `sc qfailure SoknaPrintAgent6`
- Event Viewer → System → Service Control Manager
- Event Viewer → Application → `.NET Runtime` / `Application Error`
- `%ProgramData%\Sokna\PrintAgent\health.json`
- `%ProgramData%\Sokna\PrintAgent\logs\startup-fatal.json`
- `%ProgramData%\Sokna\PrintAgent\logs\`

Service باید مستقل از Control App اجرا شود و Automatic Delayed Start + Recovery Restart داشته باشد. خرابی URL/Token/Config نباید به‌تنهایی process سرویس را terminate کند؛ این موارد باید به health/configuration error تبدیل شوند.

### queue.db قدیمی یا ناسازگار
Service قبل از واردشدن به چرخه چاپ، `queue.db` را integrity/schema-check می‌کند. از 6.2.1 رفتار recovery به شکل زیر است:

- اگر schema قدیمی/ناسازگار باشد ولی `local_jobs` و `report_outbox` هر دو صفر رکورد durable داشته باشند، فایل قبلی حذف نمی‌شود؛ با نام timestamped `queue.db.legacy-empty-...bak` حفظ و DB جاری با schema جدید ساخته می‌شود.
- اگر حتی یک رکورد durable وجود داشته باشد، reset خودکار ممنوع است. Service fail-loud می‌شود و `startup-fatal.json` دلیل را ثبت می‌کند. این محدودیت برای جلوگیری از duplicate print یا ازبین‌رفتن evidence ضروری است.
- اگر SQLite corrupt باشد یا `quick_check` موفق نباشد، DB خودکار جایگزین نمی‌شود. ابتدا Service را متوقف، فایل‌های DB/WAL/SHM را حفظ و recovery/reconciliation تصمیم‌گیری‌شده انجام دهید.

## Job blocked
Mapping مقصد/Agent/Queue را اصلاح کنید. Job required نباید حذف شود یا به‌خاطر unavailable بودن Printer ناپدید شود.

## Agent Offline
Service، اینترنت، HTTPS و credential را بررسی کنید. Offline بودن Agent نباید Print Intent سرور را حذف کند.

## Printer Offline / Paper Out
Agent نباید Job جدید را برای Queue غیرقابل‌استفاده وارد submission کند. Job سرور باقی می‌ماند. Printerهایی که فقط در User Profile نصب شده‌اند را برای LocalSystem قابل‌مشاهده فرض نکنید.

## unknown / recovery_hold
**Retry ساده یا auto-reprint ممنوع است.** Submission ممکن است به Spooler رسیده باشد. اپراتور باید یکی از Resolutionهای روشن را انتخاب کند: تأیید انجام چاپ، یا ایجاد Reprint جدید و audited.

## submitted
`submitted` فقط یعنی Windows Spooler Job را پذیرفته است؛ اثبات خروج کاغذ نیست.

## SQLite locked
Store از WAL، `synchronous=FULL` و `busy_timeout` استفاده می‌کند. Control App نباید SQLite را مستقیم mutate کند. Lock پایدار نیازمند بررسی AV/backup software/storage است.

## SQLite corrupt
Corruption باید Fail loud باشد؛ DB durable را خودکار با DB خالی جایگزین نکنید. ابتدا Service را متوقف، فایل‌ها را حفظ، Backup/diagnostic تهیه و Recovery تصمیم‌گیری‌شده انجام دهید.

## Config خراب
Service باید بدون چاپ جدید در وضعیت خطای configuration بماند؛ Jobهای سرور از بین نمی‌روند. Config را از Control App اصلاح کنید.

## Credential compromise
Token خام نباید Log شود. Credential مشکوک را از Server revoke/rotate کنید و secret جدید را از Control App ذخیره کنید.
