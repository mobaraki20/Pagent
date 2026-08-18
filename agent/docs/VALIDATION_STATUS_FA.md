# Sokna Print Agent 6 — Validation Status

وضعیت Evidence تا 2026-08-18:

## PASS اجراشده
- normal source tree `agent/` به‌عنوان Source of Truth.
- حذف Build-time source mutation از pipeline رسمی.
- Windows build/install gate در Run `32105255491` روی commit `b681223c6226608b842439080eca56447d546e26`: Restore، NuGet vulnerability audit، Build، Unit Tests، self-contained packaging، Setup `/quiet`، Service start/health، isolated paths، Automatic Delayed Start، Recovery config و Uninstall preservation همگی PASS شدند.
- در همان Run، NuGet graph نسخه `SQLitePCLRaw.bundle_e_sqlite3 2.1.12` را resolve کرد و vulnerability audit برای پروژه‌های Agent package آسیب‌پذیر گزارش نکرد.
- Windows Service crash/recovery fault test در Run `32104793517`: Service پس از kill با PID جدید برگشت و health تازه شد.
- ProgramData در uninstall پیش‌فرض حفظ شد.
- Setup Exit 1 تاریخی root-cause شد: dependency نامطمئن به `Get-FileHash` در Windows PowerShell child host. SHA-256 validation با `System.Security.Cryptography.SHA256` حفظ و مشکل رفع شد.

## قانون اعتبار این سند
هر تغییر بعدی زیر `agent/` یا pipeline رسمی باید دوباره از `build-agent.yml` عبور کند. نتیجه‌ی GitHub Actions و Artifact همان Source commit از این سند authoritative‌تر است؛ Source موجود صرفاً به دلیل وجود فایل PASS محسوب نمی‌شود.

## PENDING / UAT_REQUIRED — PRODUCTION GATE
- Machine-wide Printer Queue واقعی و visibility زیر Service account.
- Winspool و چاپ فارسی/RTL واقعی روی کاغذ.
- Kitchen / Bar / Customer receipt واقعی.
- 50 چاپ پشت‌سرهم.
- Printer Offline/Online و Paper Out.
- Spooler stop/start و queue deletion.
- Windows restart.
- internet loss/recovery در محیط عملیاتی.
- Upgrade preservation و Rollback روی Windows نصب‌شده واقعی.
- soak حداقل 24 ساعت و ترجیحاً 72 ساعت.

تا پایان این موارد، Agent **Production-ready اعلام نمی‌شود**.
