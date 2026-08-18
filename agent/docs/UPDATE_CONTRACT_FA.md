# Sokna Print Agent — Update Contract v1

این قرارداد برای جلوگیری از پیچیده‌شدن نصب و به‌روزرسانی Agent قفل می‌شود.

## قرارداد اصلی

- تنها Source of Truth پوشه `agent/` است.
- Build-time patch، استخراج Source از ZIP و mutation سورس در CI ممنوع است.
- کاربر فقط یک ورودی برای نصب و Upgrade دارد: `Sokna-Print-Agent-<version>-Setup.exe`.
- Fresh Install و Upgrade از همان installer engine استفاده می‌کنند.
- Componentهای immutable به‌صورت ایزوله زیر `%ProgramFiles%\Sokna\PrintAgent\Service`, `Worker`, `Control` قرار می‌گیرند.
- داده mutable فقط زیر `%ProgramData%\Sokna\PrintAgent` است.
- Upgrade عادی هرگز SQLite، config، credential، logs یا work state را پاک نمی‌کند.
- Payload جدید قبل از دست‌زدن به نصب live با size و SHA-256 اعتبارسنجی می‌شود.
- بعد از Upgrade، Service باید بالا بیاید و health تازه تولید کند؛ شکست باید rollback binary/config-location قبلی را تلاش کند.
- Self-Updater مستقل یا موتور Update دوم تا زمانی که مقیاس عملیاتی آن را توجیه نکند ساخته نمی‌شود.

## قانون تغییرات معمول

تغییر عادی در Service/Worker/Control/Renderer نباید نیازمند مسیر نصب جدید باشد. مسیر معمول توسعه:

`code change -> tests -> version bump -> build -> Setup.exe`

اگر یک تغییر ساده نیازمند patch script، workflow تشخیصی دائمی یا installer engine دوم شد، طراحی قبل از Merge باید بازبینی شود.

## SQLite و Rollback

- schema/version handling باید explicit باشد.
- تغییر schema همان release باید rollback عملیاتی را تا حد ممکن حفظ کند؛ destructive cleanup هم‌زمان ممنوع است.
- Upgrade باید SQLite/config موجود را حفظ کند و DB جدید را جایگزین durable state موجود نکند.
- Corruption یا incompatibility باید fail loud باشد؛ silent reset به DB خالی ممنوع است.

## CI رسمی

فقط دو workflow دائمی لازم است:

1. `build-agent.yml`: Restore/Dependency Audit/Build/Unit Test/Package + Windows Setup/Service/Uninstall Gate + artifacts.
2. `windows-reliability.yml`: fault/recovery هدفمند روی Windows.

Workflowهای diagnosis/fix موقت بعد از بسته‌شدن incident حذف می‌شوند؛ Evidence در Git history/Actions/diagnostics باقی می‌ماند.

## Production Gate

PASS در CI به معنی Production Ready کامل نیست. قبل از Production، Printer/Winspool واقعی، چاپ فارسی، Paper Out، قطع شبکه/Spooler، Windows restart، upgrade preservation، حداقل 50 چاپ متوالی و soak لازم است.
