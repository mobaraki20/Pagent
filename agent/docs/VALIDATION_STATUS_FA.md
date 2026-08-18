# Sokna Print Agent 6 — Validation Status

وضعیت ثبت‌شده در 2026-08-18:

- PASS — normal source tree `agent/` به‌عنوان Source of Truth.
- PASS — Build-time source mutation از pipeline رسمی حذف شده است.
- PASS — Hosted Windows Build + Setup + Service + Uninstall smoke. Run `32104309526`, commit `d3493477ea135a45098c6f0ffbae490ffc648aae`. Setup: stage=`completed`, exit=`0`, child exit=`0`.
- PASS — Windows Service crash/recovery fault test. Run `32104793517`, commit `876d429d81e96865b1027d34b829ab10f22920a9`; Service با PID جدید برگشته و health تازه تولید شده است.
- PASS — ProgramData در uninstall پیش‌فرض حفظ می‌شود.
- UAT_REQUIRED — چاپگر فیزیکی واقعی و visibility زیر Service account.
- UAT_REQUIRED — رسید فارسی واقعی آشپزخانه/بار/مشتری.
- UAT_REQUIRED — 50 چاپ متوالی، paper-out، spooler stop/start، network loss، service kill، upgrade از نسخه نصب‌شده و soak حداقل 24 ساعت.

تا پایان UAT فیزیکی: `PENDING — PRODUCTION GATE`.
