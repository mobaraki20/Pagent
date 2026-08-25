# Sokna Print Agent 6 — Validation Status

وضعیت Evidence تا 2026-08-25:

## PASS اجراشده — baseline 6.1.0
- normal source tree `agent/` همچنان Source of Truth است و Build-time source mutation در pipeline رسمی وجود ندارد.
- Windows build/install gate نهایی در Run `32815016816` روی commit `ad6bb5c8bc319f3cb0981ea68d177045840e76be` برای baseline `6.1.0` با نتیجه `success` کامل شد.
- در همان Run مراحل `Verify single source of truth`، Setup .NET، `Build, test and package`، `Windows install gate`، `Package tracked source`، `Verify required artifacts` و `Upload artifacts and evidence` همگی PASS شدند.
- Build رسمی شامل Restore، NuGet vulnerability audit، Build، Unit/Contract Tests و packaging است؛ failure یا vulnerability warning برای سبزکردن pipeline suppress نمی‌شود.
- Version baseline به Source of Truth واحد Build منتقل شده و artifact/runtime version در مسیر رسمی از همان owner مشتق می‌شود.
- Control App به‌عنوان WPF Operations & Diagnostics Console در همان معماری Service/Core/Worker باقی مانده و Service برای زنده‌ماندن به Control وابسته نشده است.
- Health/transport diagnostics شامل action موفق اخیر، آخرین API success/error، تعداد failure متوالی و latency است؛ heartbeat/probe observability هستند و print-path ownership ایجاد نمی‌کنند.
- durable Claim replay در SQLite اکنون envelope کامل wire body شامل `request_id`، `agent_version`، `protocol_version`، `ready_destination_keys` و `limit` را قبل از ارسال ذخیره می‌کند. Unit Test متناظر round-trip و restart را بررسی می‌کند تا Upgrade نتواند replay body را تغییر دهد.
- Logging دفاعی برای Authorization/Bearer/token/lease-token/payload/secret-like text اضافه شده و Unit Test redaction/bounding آن PASS شده است. Support Package همچنان secret/token و `queue.db` را وارد artifact نمی‌کند.

## Windows Installer / Recovery Gate
Run `32815016816` علاوه بر نصب/حذف عادی، failure recovery را در همان workflow رسمی gate کرد:
- نصب عادی از Setup واقعی، Service start، health تازه، isolated component paths، Automatic Delayed Start، Recovery configuration، Registry version، Start Menu/Desktop shortcuts و required artifacts PASS شدند.
- Uninstall پیش‌فرض Service/Program Files/shortcutها را حذف و ProgramData را حفظ کرد.
- **Synthetic Fresh Install failure** بعد از Service registration تزریق شد؛ recovery Verify کرد Windows Service orphan، Program Files نیمه‌نصب‌شده و installation registry باقی نمی‌مانند.
- **Synthetic Upgrade failure** بعد از Service registration تزریق شد؛ recovery Verify کرد Service قبلی دوباره `Running` است، binary قبلی با همان SHA-256 برگردانده شده، Registry version قبلی restore شده و ProgramData sentinel حفظ شده است.
- Setup UI فقط وقتی Rollback را موفق اعلام می‌کند که marker صریح `SOKNA_ROLLBACK_RESULT=success` پس از verification واقعی صادر شده باشد؛ وجود واژه rollback در log به‌تنهایی موفقیت محسوب نمی‌شود.

Synthetic rollback gate جای Upgrade واقعی بین دو Release نصب‌شده یا UAT پرینتر فیزیکی را نمی‌گیرد؛ فقط invariantهای recovery engine را در CI قفل می‌کند.

## Evidence قبلی معتبر
- Windows Service crash/recovery fault test در Run `32104793517`: Service پس از kill با PID جدید برگشت و health تازه شد. هر تغییر بعدی که fault/recovery semantics را تغییر دهد نیازمند evidence جدید است.

## قانون اعتبار این سند
هر تغییر بعدی زیر `agent/` یا pipeline رسمی باید دوباره از `build-agent.yml` عبور کند. نتیجه GitHub Actions و Artifact همان Source commit از این سند authoritative‌تر است؛ وجود Source یا این سند به‌تنهایی PASS محسوب نمی‌شود.

این commit فقط Evidence نهایی را با Run موفق `32815016816` هماهنگ می‌کند و خودش نیز قبل از Ready/Merge باید از CI رسمی عبور کند.

## PENDING / UAT_REQUIRED — PRODUCTION GATE
- Machine-wide Printer Queue واقعی و visibility زیر Service account.
- Winspool و چاپ فارسی/RTL واقعی روی کاغذ.
- Kitchen / Bar / Customer receipt واقعی.
- 50 چاپ پشت‌سرهم.
- Printer Offline/Online و Paper Out.
- Spooler stop/start و queue deletion.
- Windows restart.
- internet loss/recovery در محیط عملیاتی.
- Upgrade preservation و Rollback واقعی بین Releaseهای نصب‌شده روی Windows واقعی.
- soak حداقل 24 ساعت و ترجیحاً 72 ساعت.

تا پایان این موارد، Agent **Production-ready اعلام نمی‌شود** و نسخه Cafe نباید صرفاً بر مبنای CI به Release production جدید pin شود.
