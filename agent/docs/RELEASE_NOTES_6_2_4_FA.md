# Sokna Print Agent 6.2.4 — Claim Reconciliation RC

نسخه 6.2.4 ادامهٔ ایمن 6.2.3 برای رفع تعارض واقعی مشاهده‌شده روی `DESKTOP-1B25RQP` است.

- quarantine موجود نسخه 6.2.3 و request identity آن بدون حذف `queue.db` حفظ و به metadata v3 مهاجرت می‌کند.
- در صورت اعلام capability `claim_conflict_rekey_v1` از Web dev.23، Agent فقط Evidence غیرمحرمانهٔ Attempt محلی را می‌فرستد؛ payload، lease و token ارسال/Export نمی‌شوند.
- Web فقط اگر Attempt سمت Server هرگز Accept/Start نشده باشد و Attempt جدیدتر/فعال دیگری وجود نداشته باشد آن را منقضی و Attempt تازه می‌سازد. Agent سپس همان Claim را replay و فقط شناسه تازه را می‌پذیرد.
- Agent نیز مستقل از Web فقط وقتی rekey می‌خواهد که Outcome، Spooler ID، Fence و Result محلی وجود نداشته و وضعیت محلی `Reserved` یا `Resolved` بدون Evidence باشد؛ هر مدرک مبهم/Submitted به تصمیم انسانی منتهی می‌شود.
- Attempt قدیمی محلی overwrite یا حذف نمی‌شود و Auto-Reprint در وضعیت مبهم همچنان ممنوع است.
- health/support package اکنون attempt/job ID، نام فیلدهای mismatch و scope کوتاه را بدون payload/credential ثبت می‌کند.
- Acceptance جدید A30 مسیر quarantine → مهاجرت metadata v2→v3 → audited rekey → distinct replacement را پوشش می‌دهد و rekey برای Outcome محلی Submitted را رد می‌کند.

این نسخه Production deployment نیست. Real API، ارتقای واقعی همین میزبان و چاپ فیزیکی تا اجرای واقعی PASS اعلام نمی‌شوند.
