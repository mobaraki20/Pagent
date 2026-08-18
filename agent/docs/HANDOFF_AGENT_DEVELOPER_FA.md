# تحویل رسمی به توسعه‌دهنده Sokna Print Agent

این سند نقطه شروع هر برنامه‌نویس مستقلی است که قرار است Agent را نگهداری، اصلاح یا نسخه جدید منتشر کند.

## منبع رسمی

- Repository رسمی: `mobaraki20/Pagent`
- Source of Truth فقط پوشه `agent/` روی branch مورد تأیید مالک است.
- ZIP قدیمی، patch script، diagnostics قدیمی و build-time mutation جزو محصول نیستند و نباید دوباره ایجاد شوند.
- تغییر مستقیم روی `main` بدون عبور از review/CI توصیه نمی‌شود؛ تغییر در branch جدا انجام و سپس merge شود.

## ترتیب مطالعه اجباری قبل از تغییر کد

1. `README_FA.md`
2. `docs/UPDATE_CONTRACT_FA.md`
3. `docs/ARCHITECTURE_FA.md`
4. `docs/STATE_MACHINE_FA.md`
5. `docs/API_V4_FA.md`
6. `docs/SECURITY_FA.md`
7. `docs/UPGRADE_ROLLBACK_FA.md`
8. `docs/VALIDATION_STATUS_FA.md`
9. `docs/WINDOWS_PRINTER_PRODUCTION_GATE_FA.md`

برای توسعه‌دهنده‌ای که API Server را هم لمس می‌کند، `docs/HANDOFF_CAFE_SYSTEM_FA.md` نیز الزامی است.

## معماری‌ای که Protected محسوب می‌شود

`PHP/MySQL Server -> Print API v4 -> Windows Service -> SQLite durable queue -> isolated Worker -> Winspool -> Printer`

هدف سیستم:

- no silent loss
- no automatic duplicate
- explicit human resolution of ambiguity

ادعای exactly-once physical printing ممنوع است.

این اصول بدون دلیل مستند و تست جایگزین نباید شکسته شوند:

- Service واقعی Windows و مستقل از Control App
- SQLite durable queue
- Worker ایزوله
- Submission Fence durable قبل از اولین call مبهم Winspool (`StartDoc`)
- عدم Auto-Retry پس از Fence/ambiguity
- attempt history غیرقابل reset
- Reprint به‌صورت Job جدید
- ProgramData preservation در Upgrade/Uninstall پیش‌فرض
- rollback binary در Upgrade failure

## مسیر عادی یک تغییر

`branch -> code change -> tests -> version review -> build-agent CI -> Windows gate -> review -> merge`

برای تغییر عادی Service/Worker/Control/Renderer نباید workflow جدید، installer دوم یا patch script ساخته شود.

## Build رسمی

نیازمندی فعلی: .NET SDK مطابق `global.json` (در حال حاضر .NET 10).

روی Windows:

```powershell
cd agent
./scripts/Build-Agent.ps1
```

خروجی رسمی باید از همین script ساخته شود. CI دائمی:

- `.github/workflows/build-agent.yml` — Build/Test/Package + Windows install/uninstall gate
- `.github/workflows/windows-reliability.yml` — fault/recovery دستی

Workflow diagnosis موقت فقط برای incident مجاز است و بعد از حل incident باید حذف شود.

## قانون نسخه‌گذاری

در نسخه 6.0.0، version هنوز در چند نقطه hard-code است؛ حداقل:

- `scripts/Build-Agent.ps1`
- `src/Sokna.PrintAgent.Service/PrintAgentService.cs`
- `src/Sokna.PrintAgent.Setup/Sokna.PrintAgent.Setup.csproj`

این یک debt نگهداری شناخته‌شده است. قبل از اولین bump واقعی پس از 6.0.0، ترجیحاً Version باید به یک Source of Truth واحد منتقل شود و همان تغییر دوباره از Windows CI عبور کند. تا آن زمان، تغییر version باید همه نقاط بالا را هماهنگ و با artifact/runtime probe راستی‌آزمایی کند.

## تغییر API / Protocol

Agent developer حق ندارد endpoint یا state semantics را یک‌طرفه تغییر دهد.

اگر Protocol تغییر می‌کند:

- Contract اول نوشته شود.
- Server compatibility مشخص شود.
- breaking change بدون migration ممنوع است.
- minimum/recommended agent version روی Server هماهنگ شود.
- API v4 تا زمانی که migration plan تأیید نشده، شکسته نشود.

## تغییر SQLite

- schema versioned باشد.
- migration transaction-safe باشد.
- همان release با binary قبلی backward-compatible بماند تا rollback ممکن باشد.
- destructive cleanup در همان release معرفی schema جدید ممنوع است.
- Upgrade نباید DB/config موجود را با DB تازه جایگزین کند.

## تغییر Installer / Upgrade

Installer فقط وقتی تغییر کند که واقعاً installation contract تغییر کرده است. تغییر Renderer، business payload یا API logic به‌تنهایی دلیل تغییر installer نیست.

Fresh Install و Upgrade فقط از یک ورودی کاربر استفاده می‌کنند: `Sokna-Print-Agent-<version>-Setup.exe`.

در Upgrade باید:

1. payload validate شود.
2. binary جدید stage شود.
3. previous service متوقف شود.
4. swap انجام شود.
5. Service start + fresh health بررسی شود.
6. failure => rollback binary قبلی.
7. SQLite/config/token/logs/work state حفظ شوند.

## امنیت

- Production فقط HTTPS.
- token/secret خام در Git، issue، log، screenshot یا artifact ممنوع است.
- vulnerability warning نباید suppress شود تا build سبز شود.
- هر path/command/PowerShell input جدید از نظر injection بررسی شود.
- mutable state فقط در ProgramData با ACL مناسب.

## Definition of Done برای تغییر Agent

هیچ تغییر مهمی Done نیست مگر اینکه موارد مرتبط PASS باشند:

- restore/build بدون error
- unit/contract tests
- NuGet vulnerability audit
- Setup install smoke
- Service start + fresh health
- isolated Service/Worker/Control paths
- uninstall preservation
- upgrade preservation اگر installer/schema تغییر کرده
- crash/recovery اگر Service/Worker/recovery تغییر کرده
- ambiguity/no-auto-retry tests اگر spooler/state logic تغییر کرده

تست‌هایی که نیاز به پرینتر واقعی دارند اگر اجرا نشده‌اند فقط `UAT_REQUIRED` هستند؛ نباید PASS اعلام شوند.

## قبل از Release واقعی

حتماً وضعیت `docs/VALIDATION_STATUS_FA.md` و Production Gate را دوباره به‌روز کنید. برای Production، تست پرینتر واقعی، فارسی/RTL، kitchen/bar/customer receipt، printer offline/paper-out، Spooler restart، network loss، Windows restart، 50 چاپ متوالی، Upgrade واقعی و soak لازم است.

## قانون نهایی نگهداری

اگر یک تغییر ساده مجبورمان کرد patch script، workflow دائمی جدید، installer دوم یا مسیر deploy جدید بسازیم، قبل از Merge باید فرض کنیم طراحی دارد بیش‌ازحد پیچیده می‌شود و راه ساده‌تر را بررسی کنیم.
