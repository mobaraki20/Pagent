# Sokna Print Agent 6.2.4 — Claim Reconciliation RC

نسخه 6.2.4 ادامهٔ ایمن 6.2.3 برای رفع تعارض واقعی مشاهده‌شده روی `DESKTOP-1B25RQP` است.

- quarantine موجود نسخه 6.2.3 و request identity آن بدون حذف `queue.db` حفظ و به metadata v3 مهاجرت می‌کند.
- در صورت اعلام capability `claim_conflict_rekey_v1` از Web dev.23، Agent فقط Evidence غیرمحرمانهٔ Attempt محلی را می‌فرستد؛ payload، lease و token ارسال/Export نمی‌شوند.
- Web فقط اگر Attempt سمت Server هرگز Accept/Start نشده باشد آن را منقضی و Attempt تازه می‌سازد. Agent سپس همان Claim را replay و فقط شناسه تازه را می‌پذیرد.
- Attempt قدیمی محلی overwrite یا حذف نمی‌شود و Auto-Reprint در وضعیت مبهم همچنان ممنوع است.
- health/support package اکنون attempt/job ID، نام فیلدهای mismatch و scope کوتاه را بدون payload/credential ثبت می‌کند.
- Acceptance جدید A30 مسیر quarantine → audited rekey → distinct replacement را با حفظ Attempt قبلی پوشش می‌دهد.

این نسخه Production deployment نیست. Real API، ارتقای واقعی همین میزبان و چاپ فیزیکی تا اجرای واقعی PASS اعلام نمی‌شوند.
