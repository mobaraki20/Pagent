# نصب و Upgrade Sokna Print Agent 6

## وضعیت این بسته
Source و Build script آماده است؛ **ساخت و تأیید Installer نهایی باید روی Windows با .NET 10 SDK انجام شود.** فایل win-x64 تا زمانی که Build Gate پاس نشده نباید به‌عنوان Installer تأییدشده منتشر شود.

## پیش‌نیاز Production
- Windows پشتیبانی‌شده پروژه با آخرین Updateهای امنیتی.
- Printer Queue قابل مشاهده توسط Account سرویس. نسخه اولیه Service با `LocalSystem` نصب می‌شود.
- توصیه: Queue به‌صورت Machine-wide / Standard TCP/IP ایجاد شود؛ User-profile printer بدون تست Service Account مجاز نیست.
- HTTPS معتبر برای Sokna.
- برای ظاهر توافقی: `Vazirmatn` به‌صورت Machine-wide. در نبود آن Tahoma و سپس Segoe UI fallback می‌شوند.

## Build روی Windows
از ریشه `print-agent-v6` در PowerShell:

```powershell
.\scripts\Build-Agent.ps1 -Configuration Release -Runtime win-x64
```

Script باید .NET 10 SDK را enforce، restore/build/test/publish، manifest و SHA بسازد و ZIP نهایی تولید کند. Build شکست‌خورده Package قابل انتشار محسوب نمی‌شود.

## نصب
1. ZIP build‌شده را در مسیر موقت Extract کنید؛ مسیر دارای Space یا کاراکتر فارسی مجاز است.
2. PowerShell را Run as Administrator اجرا کنید.
3. `./Install-SoknaPrintAgent.ps1`
4. Installer Service را با Automatic Delayed Start، LocalSystem و Recovery Restart ایجاد می‌کند.
5. Service **حتی بدون Config/Token باید Running بماند** و `health.json` با state `waiting_for_configuration` بسازد.
6. `Sokna.PrintAgent.Control.exe` را Run as Administrator باز کنید و Server URL و Agent Token را ذخیره کنید.
7. **Restart Service لازم نیست**؛ Service تغییر Config/Secret را خودکار reload می‌کند.
8. در Control App تست API v4 و Service Health را بررسی کنید.
9. در پنل Sokna مقصدها را به Agent v4 assign کنید.
10. Test Print و سپس Production Gate واقعی را اجرا کنید.

## محل فایل‌ها
- Binary: `%ProgramFiles%\Sokna\PrintAgent`
- Config/Secret/SQLite/Logs/Health/Work: `%ProgramData%\Sokna\PrintAgent`

Installer در Upgrade داده‌های ProgramData را overwrite/delete نمی‌کند.

## Health نصب
Installer پس از Start حداکثر ۲۰ ثانیه منتظر `health.json` می‌ماند و Queueهای قابل مشاهده توسط **Service account** را گزارش می‌کند. نبود Queue Warning و Blocker برای Production Print است، نه دلیل حذف Job سرور.

## Upgrade
1. Backup از ProgramData توصیه می‌شود.
2. Installer جدید Service را stop می‌کند.
3. فقط Program Files replace می‌شود.
4. Config/secret/SQLite/logs حفظ می‌شوند.
5. Service start و Health verify می‌شود.
6. SQLite schema upgrade باید forward-compatible و بدون حذف history باشد.

## Uninstall
```powershell
.\Uninstall-SoknaPrintAgent.ps1
```
ProgramData به‌صورت پیش‌فرض حفظ می‌شود. `-RemoveData` فقط بعد از تایپ صریح `DELETE` داده‌ها را حذف می‌کند.

## Gate اجباری
تا قبل از تست Windows/Printer واقعی، این Source/Package را **Production-ready** ننامید. Checklist جداگانه `WINDOWS_PRINTER_PRODUCTION_GATE_FA.md` مرجع Promotion است.
