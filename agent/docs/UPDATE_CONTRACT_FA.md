# Sokna Print Agent — Update Contract v1

این قرارداد برای جلوگیری از پیچیده‌شدن نصب و به‌روزرسانی Agent قفل می‌شود.

## قرارداد اصلی

- تنها Source of Truth پوشه `agent/` است.
- Build-time patch، استخراج Source از ZIP و mutation سورس در CI ممنوع است.
- کاربر فقط یک ورودی برای نصب و Upgrade دارد: `Sokna-Print-Agent-<version>-Setup.exe`.
- Fresh Install و Upgrade از همان installer engine استفاده می‌کنند.
- فایل اجرایی در `Program Files\Sokna\PrintAgent` و داده mutable فقط در `ProgramData\Sokna\PrintAgent` است.
- Upgrade عادی هرگز SQLite، config، token، logs یا work state را پاک نمی‌کند.
- Binary جدید ابتدا Stage و payload با size/SHA-256 اعتبارسنجی می‌شود، سپس جایگزینی انجام می‌شود.
- بعد از Upgrade، Service باید بالا بیاید و health تازه تولید کند؛ شکست باید rollback Binary قبلی را فعال کند.
- Self-Updater مستقل یا موتور Update دوم تا زمانی که مقیاس عملیاتی آن را توجیه نکند ساخته نمی‌شود.

## قانون تغییرات معمول

تغییر عادی در Service/Worker/Control/Renderer نباید نیازمند تغییر installer یا pipeline باشد. مسیر معمول توسعه:

`code change -> tests -> version bump -> build -> Setup.exe`

اگر یک تغییر ساده نیازمند patch script، workflow جدید یا مسیر نصب جدید شد، طراحی قبل از Merge باید بازبینی شود.

## SQLite و Rollback

- migrationها versioned و transaction-safe هستند.
- migration همان release باید با Binary قبلی backward-compatible بماند تا rollback عملی باشد.
- تغییر destructive در همان release‌ای که schema جدید معرفی می‌شود ممنوع است؛ cleanup destructive حداقل یک release بعد انجام می‌شود.
- Upgrade باید SQLite/config موجود را حفظ کند و Fresh DB نسازد مگر در نصب اولیه.

## CI رسمی

فقط دو workflow دائمی مجازند:

1. `build-agent.yml`: Build، Unit Test، Package، Windows Install/Uninstall Gate و artifacts.
2. `windows-reliability.yml`: تست دستی crash/recovery روی Windows.

Workflowهای diagnosis/fix موقت بعد از بسته‌شدن incident حذف می‌شوند؛ Evidence در Git history/Actions باقی می‌ماند.

## Production Gate

PASS در CI به معنی Production Ready کامل نیست. قبل از Production، تست چاپگر واقعی، فارسی، قطع شبکه/Spooler، restart، upgrade از نسخه قبلی، بار متوالی و soak لازم است.
