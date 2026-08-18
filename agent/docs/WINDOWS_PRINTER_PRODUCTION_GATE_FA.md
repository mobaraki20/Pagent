# Windows / Printer Production Gate

این Checklist تا زمان اجرای واقعی **PENDING — PRODUCTION GATE** است.

## نصب تمیز
- [ ] Build با .NET 10 روی Windows واقعی PASS
- [ ] Installer از مسیر دارای Space PASS
- [ ] Installer از مسیر دارای کاراکتر فارسی PASS
- [ ] Service Automatic Delayed Start
- [ ] Recovery 5s / 15s / 60s تأیید
- [ ] Fresh install بدون config Running و health=`waiting_for_configuration`
- [ ] Control App بسته باشد Service ادامه دهد
- [ ] Upgrade Config/Secret/SQLite/Logs را حفظ کند
- [ ] Uninstall پیش‌فرض ProgramData را حفظ کند

## Printer account visibility
- [ ] Queue توسط LocalSystem دیده شود
- [ ] Machine-wide / Standard TCP/IP setup مستند/تأیید شود
- [ ] User-profile-only queue به اشتباه Production-approved نشود

## چاپ واقعی
- [ ] حداقل 50 فیش پشت‌سرهم
- [ ] Bar preparation
- [ ] Kitchen preparation
- [ ] Customer receipt
- [ ] Vazirmatn machine-wide و RTL واقعی
- [ ] اعداد فارسی/لاتین
- [ ] 80mm
- [ ] 58mm در صورت استفاده
- [ ] wrap متن طولانی
- [ ] ستون قیمت
- [ ] یادداشت/اصلاح/takeaway
- [ ] «چاپ مجدد» درشت
- [ ] Driver scale/fit-to-page خروجی را تغییر ندهد

## Failure recovery
- [ ] Printer Offline
- [ ] Paper Out و ادامه بعد از کاغذگذاری
- [ ] Queue pause/resume
- [ ] Queue delete/recreate
- [ ] Spooler stop/start
- [ ] Internet قطع هنگام Claim
- [ ] Internet قطع هنگام Accept
- [ ] Internet قطع هنگام Report
- [ ] Kill Service قبل Fence
- [ ] Kill Service بعد Fence
- [ ] Crash Worker هنگام Spooler
- [ ] Windows Restart
- [ ] هیچ automatic duplicate مشاهده نشود
- [ ] هیچ silent loss مشاهده نشود
- [ ] ambiguity به unknown/recovery_hold واضح برسد

## Soak
- [ ] 24 ساعت بدون silent loss
- [ ] ترجیحاً 72 ساعت

تا تکمیل همه موارد لازم، Release فقط Engineering/RC است و Production-ready نیست.
