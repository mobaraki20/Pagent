# Sokna Print Agent 6.0.0

Agent مستقل Print API v4 با هدف **عدم Silent Loss، جلوگیری از Duplicate خودکار و Resolution روشن ambiguity**؛ نه ادعای exactly-once physical printing.

## از کجا شروع کنم؟

- اگر برنامه‌نویس **سامانه کافه / Server** هستید: ابتدا `docs/HANDOFF_CAFE_SYSTEM_FA.md` را بخوانید. مرز شما Print API v4 است و برای Integration عادی نباید سورس Agent را تغییر دهید.
- اگر برنامه‌نویس **خود Print Agent** هستید: ابتدا `docs/HANDOFF_AGENT_DEVELOPER_FA.md` را بخوانید و سپس اسناد معماری/Update/State/Security معرفی‌شده در آن را دنبال کنید.

## Runtime

- `Service`: Windows Service و orchestration
- `Core`: API transport، SQLite durable queue، security و health
- `Worker`: renderer ایزوله + Winspool adapter
- `Control`: تنظیم URL/Token و health/API probe

این جداسازی بخشی از reliability چاپ است و نباید برای ساده‌سازی ظاهری flatten شود.

## نصب و Upgrade

برای کاربر فقط یک مسیر رسمی وجود دارد: `Setup.exe`. Fresh Install و Upgrade از همان مسیر انجام می‌شوند و ProgramData/SQLite حفظ می‌شود. قرارداد کامل: `docs/UPDATE_CONTRACT_FA.md`.

Source of Truth فقط پوشه `agent/` است. ZIP منبع، build-time patch و workflowهای fix/diagnose جزو معماری محصول نیستند.

## CI

- `build-agent.yml`: Build/Test/Package + Windows install gate
- `windows-reliability.yml`: fault/recovery دستی

وضعیت جاری: `docs/VALIDATION_STATUS_FA.md`.

تا تکمیل UAT چاپگر فیزیکی و fault/load/soak واقعی: `PENDING — PRODUCTION GATE`.
