# عیب‌یابی Sokna Print Agent 6

## Setup.exe خطا می‌دهد
`Setup.exe` اکنون Failure را stage-based ثبت می‌کند. ابتدا Reference ID و Stage را از stderr یا پیام Setup بردارید و سپس فایل متناظر را در این مسیر بررسی کنید:

`%ProgramData%\Sokna\PrintAgentSetup\logs\setup-<reference-id>.json`

Log شامل stage، exit code، child process exit code، exception type و stdout/stderr پاک‌سازی‌شده است. Authorization/Bearer/Token/Secret/HMAC نباید در Log ثبت شود.

Root Cause تاریخی `Setup.exe returned 1` در این پروژه `Get-FileHash` داخل Windows PowerShell child host بود. این مورد رفع شده و SHA-256 validation اکنون با `System.Security.Cryptography.SHA256` انجام می‌شود. اگر همان Stage دوباره Fail شد، آن را علت قبلی فرض نکنید؛ Evidence همان Run را بررسی کنید.

## Service نصب شده ولی بالا نمی‌ماند
- `sc query SoknaPrintAgent6`
- `sc qc SoknaPrintAgent6`
- `sc qfailure SoknaPrintAgent6`
- Event Viewer → System → Service Control Manager
- Event Viewer → Application → `.NET Runtime` / `Application Error`
- `%ProgramData%\Sokna\PrintAgent\health.json`
- `%ProgramData%\Sokna\PrintAgent\logs\`

Service باید مستقل از Control App اجرا شود و Automatic Delayed Start + Recovery Restart داشته باشد.

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
