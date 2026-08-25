# Sokna Print Agent 6 — Validation Status

وضعیت Evidence تا 2026-08-25:

## PASS اجراشده — baseline 6.1.0
- normal source tree `agent/` همچنان Source of Truth است و Build-time source mutation در pipeline رسمی وجود ندارد.
- Windows build/install gate در Run `32812776051` روی commit `39fb23ceb4ffc70bd71496647a53e11c294d808c` برای baseline `6.1.0` با نتیجه `success` کامل شد.
- در همان Run مراحل `Verify single source of truth`، Setup .NET، `Build, test and package`، `Windows install gate`، `Package tracked source`، `Verify required artifacts` و `Upload artifacts and evidence` همگی PASS شدند.
- Build رسمی شامل Restore، NuGet vulnerability audit، Build، Unit/Contract Tests و packaging است؛ failure یا vulnerability warning برای سبزکردن pipeline suppress نمی‌شود.
- Windows install gate مسیر Setup واقعی را بررسی می‌کند و Service start/health، component isolation، lifecycle نصب/حذف و preservation مورد انتظار را gate می‌کند.
- Version baseline به Source of Truth واحد Build منتقل شده و artifact/runtime version در مسیر رسمی از همان owner مشتق می‌شود.
- Control App جدید به‌عنوان Operations & Diagnostics Console در همان معماری Service/Core/Worker باقی مانده و Service برای زنده‌ماندن به Control وابسته نشده است.
- durable claim replay metadata برای replay همان `request_id` و همان body بعد از interruption در SQLite نگه‌داری می‌شود و Unit Test متناظر به suite اضافه شده است.
- Windows Service crash/recovery fault test قبلی در Run `32104793517`: Service پس از kill با PID جدید برگشت و health تازه شد؛ هر تغییر بعدی در fault/recovery که semantics را عوض کند نیازمند evidence جدید است.

## قانون اعتبار این سند
هر تغییر بعدی زیر `agent/` یا pipeline رسمی باید دوباره از `build-agent.yml` عبور کند. نتیجه GitHub Actions و Artifact همان Source commit از این سند authoritative‌تر است؛ وجود Source یا این سند به‌تنهایی PASS محسوب نمی‌شود.

این commit فقط Evidence مستند را با Run موفق `32812776051` هماهنگ می‌کند و قبل از Merge باید خودش نیز از CI عبور کند.

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

تا پایان این موارد، Agent **Production-ready اعلام نمی‌شود** و نسخه Cafe نباید صرفاً بر مبنای CI به Release production جدید pin شود.
