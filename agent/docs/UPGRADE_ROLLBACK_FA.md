# Upgrade و Rollback — Sokna Print Agent 6 / Sokna 1.33.0-rc1

## اصل
Migration `migrations/1.33.0-print-agent-v4.sql` افزایشی است و history چاپ نباید حذف یا overwrite شود. Rollback عملیاتی به معنی بازگرداندن mapping/destination با رعایت ownership فعلی Jobهاست؛ پاک‌کردن `print_attempts` یا Audit برای rollback مجاز نیست.

## Rollout تدریجی
1. Backup و recovery plan دیتابیس/ProgramData آماده شود.
2. Migration `1.33.0-print-agent-v4.sql` روی staging واقعی MySQL/MariaDB تست و سپس طبق فرآیند Release اجرا شود.
3. Agent 6 نصب شود ولی destinationهای Production هنوز به آن assign نشوند.
4. Service/health/API v4 و Printer visibility تحت Service Account بررسی شود.
5. یک destination کم‌ریسک assign و Test Print واقعی اجرا شود.
6. پس از عبور UAT، destinationهای دیگر یکی‌یکی مهاجرت کنند.

## Upgrade خود Agent 6
Installer staged عمل می‌کند:
- Service قبلی stop می‌شود.
- Binaryهای جدید در layout ایزوله Service/Worker/Control جایگزین می‌شوند.
- Registry locationها و Service config به نسخه جدید اشاره می‌کنند.
- `%ProgramData%\Sokna\PrintAgent` حفظ می‌شود.
- Service start و health تازه verify می‌شود.
- در Failure، binary/config-location rollback تلاش می‌شود؛ durable data نباید حذف شود.

قبل از Upgrade واقعی، preservation حداقل برای `config.json`, `secret.dat`, `queue.db`, logs و work state تست شود.

## Rollback قبل از ownership مبهم
Jobهای `pending/blocked` که هنوز ownership submission ندارند می‌توانند پس از اصلاح mapping در مسیر مجاز ادامه یابند.

## Rollback با Job پذیرفته‌شده v4
Job/Attemptهای `claimed`, `unknown`, `recovery_hold` و هر موردی که Submission ambiguity دارد را به Agent/Printer دیگر auto-reroute نکنید. ابتدا human resolution لازم است. اگر Reprint لازم شد، Job/Attempt جدید و audited ایجاد شود؛ Attempt قبلی reset نشود.

`submitted` فقط پذیرش Windows Spooler است و اثبات چاپ فیزیکی نیست.

## Rollback Agent binary
اگر Upgrade Agent جدید قبل از submission مشکل ایجاد کرد، از binary package تأییدشده قبلی استفاده کنید و ProgramData موجود را حفظ کنید. اگر SQLite schema نسخه جدید forward-only شده باشد، rollback binary بدون compatibility verification ممنوع است.

## Schema rollback
Schema additive را صرفاً برای برگشت نسخه drop نکنید. Downgrade کد باید روی staging با همان Schema افزوده تست شود. Destructive DROP فقط در محیط غیرتولیدی بدون history موردنیاز و با Backup معتبر قابل‌قبول است.

## Gate قبل از Production
- upgrade preservation واقعی
- rollback واقعی
- Service kill/recovery
- Windows restart
- Printer offline/online و Paper Out
- Spooler stop/start
- ambiguity بدون auto duplicate
- چاپ فارسی واقعی
- soak

تا اجرای این موارد، Rollout نهایی **PENDING / UAT_REQUIRED — PRODUCTION GATE** است.
