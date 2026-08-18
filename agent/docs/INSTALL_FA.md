# نصب Sokna Print Agent 6.0.0

## وضعیت
Windows Build و Installer smoke در CI واقعی اجرا و PASS شده‌اند. این فقط Installation Gate است؛ چاپ فیزیکی واقعی، Printer/Spooler faultها، Windows restart و soak همچنان **PENDING / UAT_REQUIRED — PRODUCTION GATE** هستند.

## پیش‌نیاز Production
- Windows پشتیبانی‌شده و به‌روز.
- Printer Queue قابل مشاهده توسط Account سرویس. Agent فعلی با `LocalSystem` نصب می‌شود.
- ترجیحاً Machine-wide / Standard TCP/IP Queue؛ User-profile printer بدون verification مجاز نیست.
- HTTPS معتبر برای Sokna production.
- Agent credential معتبر و قابل revoke/rotate.
- در صورت نیاز به Vazirmatn، فونت باید Machine-wide باشد؛ Agent به User Profile وابسته نیست.

## Artifactهای Windows
Build رسمی باید این موارد را با SHA-256 همان Run تولید کند:
- `Sokna-Print-Agent-6.0.0-Setup.exe`
- `Sokna-Print-Agent-6.0.0-win-x64.zip`
- `Sokna-Print-Agent-6.0.0-source.zip`
- `SHA256SUMS-Agent-6.0.0.txt`
- `BUILD_ARTIFACTS-Agent-6.0.0.json`

## نصب پیشنهادی
1. SHA-256 `Setup.exe` را با گزارش Build همان Run تطبیق دهید.
2. `Sokna-Print-Agent-6.0.0-Setup.exe` را با دسترسی Administrator اجرا کنید. برای نصب silent: `/quiet`.
3. Setup payload embedded را استخراج و SHA-256 همه فایل‌های Manifest را قبل از تغییر سیستم Verify می‌کند.
4. Installer Service `SoknaPrintAgent6` را با `Automatic Delayed Start` و Windows Recovery Restart ایجاد می‌کند.
5. Service باید حتی بدون Config/Token بالا بماند و `health.json` با حالت waiting/configuration state بنویسد.
6. Control App را فقط برای تنظیم Server URL/credential و بررسی سلامت باز کنید. Control برای زنده‌ماندن Service لازم نیست.
7. در پنل Sokna destinationها را assign کنید.
8. قبل از Production، Test Print و checklist چاپگر واقعی را اجرا کنید.

## Layout نصب
- Service: `%ProgramFiles%\Sokna\PrintAgent\Service\`
- Worker: `%ProgramFiles%\Sokna\PrintAgent\Worker\`
- Control: `%ProgramFiles%\Sokna\PrintAgent\Control\`
- Uninstaller: `%ProgramFiles%\Sokna\PrintAgent\Uninstall-SoknaPrintAgent.ps1`
- Mutable state: `%ProgramData%\Sokna\PrintAgent\`
- Setup diagnostics: `%ProgramData%\Sokna\PrintAgentSetup\logs\`

Mutable state شامل config/secret/SQLite/logs/work/health است و Upgrade نباید آن را پاک کند.

## Health نصب
Installer پس از Service start منتظر health تازه می‌ماند. نبود Printer Queue قابل مشاهده توسط LocalSystem Warning/Production Blocker است، نه دلیل حذف Job Server.

## Uninstall
Uninstaller Service را stop/delete می‌کند و منتظر حذف واقعی SCM می‌ماند. Program Files حذف می‌شود اما ProgramData به‌صورت پیش‌فرض حفظ می‌شود.

حذف داده فقط با `-RemoveData` و تأیید صریح `DELETE` مجاز است.

## Build برای توسعه
از ریشه `agent/` روی Windows:

```powershell
.\scripts\Build-Agent.ps1 -Configuration Release -Runtime win-x64
```

Release CI مستقیماً همین normal source tree را Build می‌کند؛ ZIP اولیه و patch-time mutation نباید در مسیر Release استفاده شوند.
