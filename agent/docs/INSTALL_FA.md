# نصب و به‌روزرسانی Sokna Print Agent 6.1.2

## وضعیت
Windows Build و Setup/Service/Uninstall gate نسخه 6.1 روی branch توسعه اجرا و PASS شده‌اند. این فقط Installation/Upgrade engineering gate است؛ چاپ فیزیکی واقعی، Printer/Spooler faultها، Windows restart و soak همچنان **PENDING / UAT_REQUIRED — PRODUCTION GATE** هستند.

## اصل نصب
برای کاربر فقط یک ورودی وجود دارد:

`Sokna-Print-Agent-<version>-Setup.exe`

Fresh Install و Upgrade از همان engine استفاده می‌کنند. Setup UI فقط Stageهای واقعی همان engine را نمایش می‌دهد و updater/service دوم وجود ندارد.

## پیش‌نیاز Production
- Windows پشتیبانی‌شده و به‌روز.
- Printer Queue قابل مشاهده توسط Account سرویس؛ Agent با `LocalSystem` اجرا می‌شود.
- ترجیحاً Machine-wide / Standard TCP/IP Queue؛ User-profile printer بدون verification مجاز نیست.
- HTTPS معتبر برای Sokna production.
- Agent credential معتبر و قابل revoke/rotate.
- فونت Vazirmatn داخل خود Worker بسته‌بندی و به‌صورت Private Font بارگذاری می‌شود؛ نصب دستی یا Machine-wide لازم نیست.

## Artifactهای Windows
Build رسمی Version را از `Directory.Build.props:SoknaAgentVersion` می‌گیرد و باید Artifactهای زیر را با SHA-256 همان Run تولید کند:

- `Sokna-Print-Agent-6.1.2-Setup.exe`
- `Sokna-Print-Agent-6.1.2-win-x64.zip`
- `Sokna-Print-Agent-6.1.2-source.zip`
- `SHA256SUMS-Agent-6.1.2.txt`
- `BUILD_ARTIFACTS-Agent-6.1.2.json`

## تجربه نصب / Upgrade
Setup قبل از تغییر سیستم:

1. نوع Fresh/Upgrade و Version فعلی/هدف را تشخیص می‌دهد.
2. payload embedded را استخراج می‌کند.
3. Manifest، size و SHA-256 همه فایل‌ها را Verify می‌کند.
4. ProgramData و ACL را آماده/حفظ می‌کند.
5. نسخه جدید را خارج از مسیر live stage می‌کند.
6. Service قبلی را کنترل‌شده متوقف و binary swap را با backup انجام می‌دهد.
7. Registry Version/path، Windows Service، Automatic Delayed Start و Recovery را Verify می‌کند.
8. Service جدید را Start می‌کند و منتظر `health.json` تازه می‌ماند.
9. Start Menu و Desktop shortcut را به Operations & Diagnostics Console ثبت می‌کند.
10. فقط بعد از Health موفق backup موقت را finalize می‌کند.

UI Setup Progress واقعی همین Stageها را نشان می‌دهد. Failure باید Stage و Reference ID قابل پیگیری داشته باشد؛ Error خام/Token در UI یا log مجاز نیست.

## Upgrade preservation
Upgrade عادی نباید این‌ها را حذف یا reset کند:

- `config.json`
- `secret.dat`
- `queue.db`
- Agent logs
- work/fence/result state
- report outbox

در Failure، binary/config-location rollback نسخه قبلی تلاش می‌شود و durable data حفظ می‌شود.

## Layout نصب
- Service: `%ProgramFiles%\Sokna\PrintAgent\Service\`
- Worker: `%ProgramFiles%\Sokna\PrintAgent\Worker\`
- Operations Console: `%ProgramFiles%\Sokna\PrintAgent\Control\`
- Uninstaller: `%ProgramFiles%\Sokna\PrintAgent\Uninstall-SoknaPrintAgent.ps1`
- Mutable state: `%ProgramData%\Sokna\PrintAgent\`
- Setup diagnostics: `%ProgramData%\Sokna\PrintAgentSetup\logs\`

Mutable state فقط در ProgramData است و Upgrade نباید آن را با دیتای خالی جایگزین کند.

## Operations Console
بعد از Fresh Install، Shortcut استاندارد `Sokna Print Agent` در Start Menu و Desktop وجود دارد. Console برای:

- Health و وضعیت Service/API
- Printer/Queue visibility
- Test Center
- Agent/Setup logs و Event Viewer
- Support Package redacted
- Server URL و Credential rotation

استفاده می‌شود.

با بستن پنجره، Console در System Tray باقی می‌ماند و با دوبارکلیک روی آیکن دوباره باز می‌شود. گزینهٔ «خروج کامل» در منوی Tray فقط Console را می‌بندد؛ Print Runtime مستقلِ Windows Service متوقف نمی‌شود.

## Health نصب
Installer پس از Service start منتظر health تازه می‌ماند. نبود Printer Queue قابل مشاهده توسط LocalSystem Warning/Production Blocker است، نه دلیل حذف Job Server.

## Silent install
برای CI/Automation همان Setup رسمی با `/quiet` اجرا می‌شود؛ موتور نصب متفاوتی وجود ندارد.

## Uninstall
Uninstaller Service را stop/delete می‌کند، Shortcutها و Program Files را حذف می‌کند ولی ProgramData را به‌صورت پیش‌فرض حفظ می‌کند.

حذف داده فقط با `-RemoveData` و تأیید صریح `DELETE` مجاز است.

## Build برای توسعه
از ریشه `agent/` روی Windows:

```powershell
.\scripts\Build-Agent.ps1 -Configuration Release -Runtime win-x64
```

Release CI مستقیماً همین normal source tree را Build می‌کند؛ ZIP اولیه، build-time source mutation و patch-time mutation در مسیر Release ممنوع‌اند.
