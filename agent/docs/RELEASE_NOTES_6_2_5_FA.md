# یادداشت انتشار Sokna Print Agent 6.2.5

## علت ریشه‌ای

در 6.2.4، `PrintAgentService.AcceptAnyReservedAsync` فقط برای بازیابی Accept منقضی‌شده از `attempt_status` استفاده می‌کرد. هیچ حلقه‌ای برای outcome پایدار `Unknown/RecoveryHold` یا report با `ReconciliationRequired` وجود نداشت؛ بنابراین تصمیم انسانی authoritative در Server مصرف نمی‌شد. `ClaimAsync` نیز فقط سلامت Windows queue را می‌دید و destination دارای blocker محلی را حذف نمی‌کرد. در نتیجه کارهای بعدی همان مقصد Claim/Accept می‌شدند ولی `ProcessOneAsync` پشت رکورد قدیمی متوقف می‌ماند.

تشخیص printer نیز صرفاً بر پایه flagهای Windows (`offline/paused/paper/error`) بود. Queue تعاملی `Microsoft Print to PDF` روی `PORTPROMPT:` به اشتباه Ready محسوب می‌شد، در حالی که Windows Service تحت `LocalSystem` امکان نمایش Save As ندارد.

## اصلاحات

- حلقه‌ی reconciliation برای `Unknown/RecoveryHold` با `attempt_status` اضافه شد.
- فقط پاسخ دارای identity و receipt منطبق، `terminal=true`، `requires_human_resolution=false` و `next_action=none` blocker را settle می‌کند.
- outcome اصلی حذف یا به `submitted` تبدیل نمی‌شود؛ report محلی با state جدید `SettledByServerResolution` از ارسال مجدد خارج می‌شود.
- `local_unknown_count` اکنون فقط رکوردهای محلی واقعاً unresolved را می‌شمارد.
- destination دارای blocker از `ready_destination_keys` حذف می‌شود.
- Jobهای Claimed که پشت blocker بوده‌اند با marker پایدار مشخص و قبل از Worker launch با Server اعتبارسنجی می‌شوند؛ terminal/reconcile چاپ نمی‌شود و مسیر عادی Start replay تغییر نمی‌کند.
- `PORTPROMPT:`, `FILE:`, `SHRFAX:` و `NUL:` و همچنین Microsoft Print to PDF/XPS برای unattended printing نامعتبرند.
- Queue داخلی `Sokna PDF Test` برای UAT بدون تغییر باقی مانده است.
- mapping قدیمی تعاملی قبل از `start` و Worker با خطای `printer_not_automation_capable` متوقف می‌شود.

## Upgrade و Rollback

Schema version تغییر نکرده است. Installer باید `config.json`، secret/pairing، `queue.db`، outbox و audit را حفظ کند. حذف یا reset کردن `queue.db` ممنوع است.

Rollback باید با Installer نسخه قبلی و حفظ ProgramData انجام شود. اگر state جدید `SettledByServerResolution` برای binary قدیمی قابل تفسیر نیست، Service قدیمی باید خاموش بماند تا 6.2.5 دوباره نصب شود؛ DB نباید پاک شود.

## Incident transition مورد انتظار

- Job29/Attempt49: `Unknown` به `Resolved` منتقل می‌شود؛ outcome مبهم حفظ و outbox به `SettledByServerResolution` می‌رود.
- Job30/Attempt50 و Job31/Attempt51: پس از رفع blocker، هرکدام پیش از Worker با `attempt_status` بررسی می‌شوند. فقط `claimed + receipt match + next_action=start` ادامه می‌یابد؛ terminal یا human-resolution چاپ نمی‌شود.
