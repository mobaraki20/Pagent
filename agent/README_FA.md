# Sokna Print Agent 6.1.0

Agent مستقل Print API v4 با هدف **عدم Silent Loss، جلوگیری از Duplicate خودکار و Resolution روشن ambiguity**؛ نه ادعای exactly-once physical printing.

## از کجا شروع کنم؟

- اگر برنامه‌نویس **سامانه کافه / Server** هستید: ابتدا `docs/HANDOFF_CAFE_SYSTEM_FA.md` را بخوانید. مرز شما Print API v4 است و برای Integration عادی نباید سورس Agent را تغییر دهید.
- اگر برنامه‌نویس **خود Print Agent** هستید: ابتدا `docs/HANDOFF_AGENT_DEVELOPER_FA.md` را بخوانید و سپس اسناد معماری/Update/State/Security معرفی‌شده در آن را دنبال کنید.
- تغییرات Stabilization/Operations Console نسخه 6.1 در `docs/STABILIZATION_6_1_0_FA.md` ثبت شده‌اند.

## Runtime

- `Service`: Windows Service و orchestration؛ مستقل از UI و مالک اجرای مسیر عملیاتی.
- `Core`: API transport، SQLite durable queue، security و health.
- `Worker`: renderer ایزوله + Winspool adapter.
- `Control`: **Operations & Diagnostics Console** برای Health، Printer visibility، تست‌های مرحله‌ای، Logs، Support Package و تنظیمات اتصال.

این جداسازی بخشی از reliability چاپ است و نباید برای ساده‌سازی ظاهری flatten شود. بسته‌شدن Control Console هیچ اثری بر Service/Worker ندارد.

## نصب و Upgrade

برای کاربر فقط یک مسیر رسمی وجود دارد: `Setup.exe`. Fresh Install و Upgrade از همان installer engine انجام می‌شوند؛ Setup مراحل واقعی، Health validation و Failure reference را نمایش می‌دهد و ProgramData/SQLite را حفظ می‌کند. Start Menu/Desktop فقط به Control Console اشاره می‌کنند. قرارداد کامل: `docs/UPDATE_CONTRACT_FA.md`.

Source of Truth فقط پوشه `agent/` است. ZIP منبع، build-time patch و workflowهای fix/diagnose جزو معماری محصول نیستند. Version فقط از `Directory.Build.props` مدیریت می‌شود.

## Reliability transport در 6.1

- Claim در timeout/restart با **همان request_id و همان body** از SQLite replay می‌شود؛ Server idempotency واقعاً مصرف می‌شود.
- Probe/Heartbeat و refresh تشخیصی حق ندارند مسیر حیاتی Accept/Print/Report را starve کنند.
- Logهای transport action-aware هستند (`claim`, `accept`, `start`, `report`, `heartbeat`, `probe_refresh`).
- Health محلی و Heartbeat Evidence آخرین action/success/error/consecutive failure/latency را بدون Secret گزارش می‌کنند.

## CI

- `build-agent.yml`: Restore / vulnerability audit / Build / Unit Test / Package + Windows Setup/Service/Shortcut/Uninstall gate.
- `windows-reliability.yml`: fault/recovery دستی.

وضعیت جاری: `docs/VALIDATION_STATUS_FA.md`.

تا تکمیل UAT چاپگر فیزیکی و fault/load/soak واقعی: `PENDING — PRODUCTION GATE`.
