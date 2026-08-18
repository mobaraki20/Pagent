# Sokna Print Agent 6.0.0 — Source Clean-slate

Agent جدید مستقل از Binary نسخه 5 است و برای Print API v4 ساخته شده است.

اجزا:
- `Sokna.PrintAgent.Service`: Windows Service و orchestration
- `Sokna.PrintAgent.Core`: transport، SQLite durable queue، models، DPAPI، health
- `Sokna.PrintAgent.Worker`: isolated renderer + Winspool adapter
- `Sokna.PrintAgent.Control`: تنظیم URL/Token، API probe و Service health
- `installer/`: نصب/حذف preserving ProgramData
- `scripts/Build-Agent.ps1`: Build/Test/Publish .NET 10
- `docs/`: معماری، API، state machine، امنیت، نصب، rollback و Production Gate

تضمین سیستم: **عدم Silent Loss، جلوگیری از Duplicate خودکار و Resolution روشن برای ambiguity**؛ نه Exactly-once physical printing.

وضعیت فعلی: Source و تست‌های static/model در محیط توسعه آماده‌اند؛ Build و Printer UAT واقعی فقط روی Windows انجام می‌شود و تا آن زمان `PENDING — PRODUCTION GATE` است.
