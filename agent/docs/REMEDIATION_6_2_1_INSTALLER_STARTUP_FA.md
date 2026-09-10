# Remediation نصب و Startup — Sokna Print Agent 6.2.1

## دامنه و شواهد مسئله
این remediation بر اساس دو failure واقعی روی Windows طراحی شد:

1. نصب/repair نسخه 6.2.0 در مرحله `program_files_swap` با `Access denied` روی `C:\Program Files\Sokna\PrintAgent` متوقف شد و Desktop shortcut تا زمان اصلاح دستی دسترسی پوشه توسط کاربر قابل استفاده نبود.
2. پس از اصلاح دستی ACL، همان نصب از swap عبور کرد ولی در `service_start` با `Service did not stay running: Stopped` متوقف شد و recovery سرویس قبلی نیز قابل تأیید نبود.

هدف این تغییر، حذف workaround دستی، افزایش قابلیت repair/upgrade و تشخیص دقیق startup failure بدون قربانی‌کردن evidence چاپ یا امنیت ProgramData است.

## Root Cause و تصمیم منتخب
### ACL / Shortcut / Program Files
Installer 6.2.0 inheritance را حذف می‌کرد و روی Program Files فقط `SYSTEM:RX` و `Administrators:F` می‌گذاشت. Explorer و resolve آیکن/target شورتکات در context عادی کاربر اجرا می‌شوند؛ بنابراین نبودن `Users:RX` یک policy اشتباه برای برنامه‌ای با public Desktop/Start Menu shortcut بود.

راه‌حل منتخب:
- ACL با SIDهای ثابت Windows تنظیم می‌شود تا به زبان OS وابسته نباشد.
- Program Files: `SYSTEM:RX`, `Administrators:F`, `Users:RX`.
- ProgramData: فقط `SYSTEM:F`, `Administrators:F`.
- هیچ Write/Modify به Users داده نمی‌شود.
- repair قبل از swap، ACL نصب موجود را اصلاح می‌کند.

### Repair/Upgrade swap
فقط توقف Control App برای آزادشدن تمام فایل‌های Agent کافی نبود. Worker می‌توانست transient handle داشته باشد و rename نیز نسبت به lockهای کوتاه Explorer/AV حساس بود.

راه‌حل منتخب:
- Control و Worker پیش از swap متوقف می‌شوند.
- Service باید واقعاً به `Stopped` برسد.
- parent path با create/delete probe بررسی می‌شود.
- rename/move با retry محدود انجام می‌شود.
- rollback نیز از همان move-with-retry استفاده کرده و ACL نسخه restoreشده را به policy سالم برمی‌گرداند.

### Service startup / queue.db
Startup پیش از حلقه اصلی، دیتابیس محلی را باز و schema را verify می‌کند. failure در این ناحیه می‌توانست process سرویس را terminate کند، در حالی که Setup فقط `Stopped` می‌دید و علت واقعی قابل مشاهده نبود.

راه‌حل منتخب دو بخش دارد:
1. top-level startup diagnostic در `logs/startup-fatal.json` و daily Agent log؛ Installer هنگام failure همین علت sanitizeشده را surface می‌کند.
2. bootstrap محافظه‌کارانه برای DB قدیمی:
   - current schema: بدون تغییر.
   - legacy/incompatible و کاملاً خالی: DB قبلی به‌عنوان backup حفظ و schema جاری از نو ساخته می‌شود.
   - legacy/incompatible با هر durable row: reset خودکار ممنوع و fail-loud.
   - corrupt/integrity failure: reset خودکار ممنوع و fail-loud.

## Review مستقل تیم‌ها
### Software Engineering / Architecture
راه‌حل باید failure domain نصب را از durable print state جدا نگه دارد. حذف ساده `queue.db` رد شد چون می‌تواند identity و evidence attempt را از بین ببرد. bootstrap فقط زمانی destructive-to-active-path است که zero durable rows اثبات شده باشد؛ فایل قبلی همچنان backup می‌شود.

### Security
دادن Full Control یا Modify به `Users` رد شد. `Read & Execute` حداقل privilege لازم برای public shortcut و اجرای Control با UAC است. Secret/config/data همچنان زیر ProgramData با ACL محدود باقی می‌مانند. SID به‌جای نام localized group انتخاب شد.

### Cafe / Restaurant Operations
اپراتور نباید برای نصب به Properties/Security پوشه Program Files برود. repair همان نسخه باید بدون knowledge فنی کار کند. در مقابل، اگر queue دارای evidence حل‌نشده باشد، توقف و درخواست reconciliation از reset خودکار بهتر است چون چاپ تکراری فاکتور/آشپزخانه از عدم convenience خطرناک‌تر است.

### QA / Test
Regression gate باید دقیقاً failure واقعی را بازتولید کند: پس از fresh install عمداً `Users:RX` حذف شود و همان Setup دوباره اجرا گردد. PASS فقط وقتی است که repair exit=0، Service Running، ACL restored و shortcut موجود باشد. Unit test جداگانه legacy-empty و legacy-with-durable-row را پوشش می‌دهد.

### DB / Data Integrity
`PRAGMA quick_check(1)` پیش از هر تصمیم recovery اجرا می‌شود. DB corrupt هرگز به‌صورت silent reset نمی‌شود. وجود حتی یک row در `local_jobs` یا `report_outbox` reset را مسدود می‌کند.

### DevOps / Release
Build اصلاح‌شده باید با version جدید `6.2.1` ساخته شود؛ نباید binary متفاوت با label رسمی 6.2.0 منتشر شود. Windows build/install gate و artifact checksum همچنان gate انتشار هستند.

### UX / Diagnostics
پیام `Service did not stay running: Stopped` به‌تنهایی actionable نیست. `startup-fatal.json` و propagation علت به Installer باعث می‌شود fault بعدی بدون Event Viewer guesswork قابل تشخیص باشد، در حالی که متن قبل از log sanitize می‌شود.

## Review Round 1 — نقد راه‌حل منتخب
- خطر: `Users:RX` ممکن است سطح دسترسی بیش از حد بدهد. نتیجه: فقط code binaries قابل خواندن/اجرا هستند؛ writable/secrets در ProgramData نیستند. policy پذیرفته شد.
- خطر: auto-reinitialize DB قدیمی duplicate print بسازد. نتیجه: فقط zero durable rows مجاز است و DB قبلی backup می‌شود؛ در غیر این صورت fail-loud. policy پذیرفته شد.
- خطر: retry بی‌نهایت install را hang کند. نتیجه: تعداد attempt محدود و backoff bounded است.
- خطر: failure هنگام rollback دوباره ACL قبلی خراب را restore کند. نتیجه: بعد از restore، ACL policy سالم دوباره اعمال می‌شود.
- خطر: config/token خراب service را terminate کند. معماری فعلی این خطاها را داخل health/configuration_error نگه می‌دارد و remediation آن رفتار را تغییر نمی‌دهد.

## Review Round 2 — بازبینی نهایی
- Data safety: هیچ مسیر جدیدی DB دارای row یا DB corrupt را حذف نمی‌کند.
- Duplicate-print safety: attempt/outbox evidence در صورت وجود، migration/reset خودکار ندارد.
- Least privilege: Users فقط RX؛ ProgramData تغییری به سمت بازترشدن ندارد.
- Repairability: same-version repair اکنون ACL موجود را قبل از rename اصلاح می‌کند و handles شناخته‌شده بسته می‌شوند.
- Observability: startup exception پیش از خروج process در فایل مستقل ثبت می‌شود و Setup آن را می‌خواند.
- Testability: unit coverage و real Setup repair regression در Windows gate اضافه شده است.

نتیجه Review Round 2: **راه‌حل برای CI/UAT مناسب است، اما Production approval فقط پس از PASS کامل Windows CI و یک نصب/repair واقعی روی PC هدف انجام شود.**

## فایل‌های تغییرکرده
- `agent/Directory.Build.props`
- `agent/installer/Install-SoknaPrintAgent.ps1`
- `agent/src/Sokna.PrintAgent.Core/QueueDatabaseBootstrap.cs`
- `agent/src/Sokna.PrintAgent.Service/Program.cs`
- `agent/tests/Sokna.PrintAgent.Tests/Program.cs`
- `agent/scripts/Test-Windows-Install.ps1`
- `agent/docs/TROUBLESHOOTING_FA.md`

## Acceptance عملی مورد انتظار
روی ماشین هدف:
1. Setup 6.2.1 روی نصب 6.2.0 موجود اجرا شود؛ کاربر هیچ ACL را دستی تغییر ندهد.
2. Setup باید از `program_files_swap` عبور کند، Service را Running نگه دارد و health جدید بسازد.
3. Desktop shortcut باید بدون بازکردن دستی Program Files آیکن را resolve کند و Control App را با UAC اجرا کند.
4. اجرای مجدد Setup 6.2.1 روی 6.2.1 نیز باید به‌عنوان repair موفق شود.
5. اگر startup fail شد، JSON نصب باید علت برگرفته از `startup-fatal.json` را گزارش کند؛ `queue.db` و فایل‌های جانبی آن تا زمان تصمیم recovery حذف نشوند.
