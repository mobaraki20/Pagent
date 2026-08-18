# تحویل رسمی به برنامه‌نویس سامانه کافه — Print Agent 6 / API v4

این سند مرز مسئولیت سامانه کافه و Sokna Print Agent را مشخص می‌کند. برنامه‌نویس سامانه کافه برای اتصال چاپ **نباید سورس Agent را تغییر دهد**؛ قرارداد اتصال فقط Print API v4 است.

## بسته‌ای که باید تحویل بگیرد

1. سورس رسمی و جاری سامانه کافه. در زمان این Handoff، baseline مالک: `Sokna 1.33.0-rc1`.
2. این سند.
3. `API_V4_FA.md` — قرارداد endpointها، payloadها، idempotency و auth.
4. `STATE_MACHINE_FA.md` — stateها و transitionهای مجاز.
5. در صورت نیاز به درک عمیق‌تر: `ARCHITECTURE_FA.md`.
6. Token/credential محیط تست فقط از مسیر امن و خارج از Git/Documentation.

## مسئولیت سامانه کافه

- Print intent لازم باید همراه عملیات کسب‌وکار مربوطه به‌صورت Durable در DB ثبت شود؛ برای سفارش/عملیاتی که چاپ بخشی از نتیجه آن است، از network call مستقیم به Agent داخل transaction کسب‌وکار استفاده نشود.
- Server مالک state machine چاپ و Attempt history است.
- Mutationهای Print API باید با `request_id` idempotent باشند.
- `reserved` فقط lease موقت است و مجوز چاپ نیست.
- `claimed` با قطع heartbeat نباید خودکار به Agent دیگری واگذار شود.
- `submitted` فقط به معنی پذیرش توسط Windows Spooler است، نه تأیید چاپ فیزیکی.
- `unknown` و `recovery_hold` هرگز Auto-Reprint ندارند.
- Reprint همیشه Job/Attempt جدید و Audit‌شده می‌سازد؛ Job قبلی reset نمی‌شود.
- Resolution انسانی ambiguity باید reason/user/time داشته باشد.
- Destination configuration شامل queue name، paper width، printable width، copies و layout باید server-authoritative باشد.

## چیزهایی که ممنوع است

- تماس مستقیم Order transaction با Windows Service/Agent برای «اطمینان از چاپ».
- reset کردن `submitted/unknown/recovery_hold` به `pending`.
- Retry خودکار بعد از ambiguity.
- استفاده از یک Attempt قدیمی برای Reprint.
- فرض exactly-once physical printing.
- ذخیره Token خام در log یا responseهای مدیریتی.
- تغییر Agent برای حل یک نیاز صرفاً UI/Server بدون تغییر رسمی قرارداد API.

## Definition of Done سمت سامانه کافه

حداقل این سناریوها باید تست خودکار داشته باشند:

1. ثبت سفارش موفق + ثبت durable print intent.
2. rollback سفارش => print intent یتیم ساخته نشود.
3. Agent/Internet قطع است ولی intent از بین نمی‌رود.
4. replay همان `request_id` state یا print جدید نمی‌سازد.
5. `pending -> reserved -> claimed -> started -> submitted` معتبر است.
6. lease expiry فقط در `reserved` قابل آزادسازی امن است.
7. heartbeat loss روی `claimed/started` باعث reassignment خودکار نمی‌شود.
8. terminal state دوباره Start authorization نمی‌گیرد.
9. `unknown/recovery_hold` Auto-Retry نمی‌شود.
10. Reprint یک Job جدید با `reprint_of_id` و Audit می‌سازد.
11. FIFO destination fence مانع عبور Job جدیدتر از exception حل‌نشده می‌شود مگر override مجاز و audit‌شده.
12. Token invalid/expired و permission failure fail-closed هستند.

## مرز تغییر Protocol

اگر نیاز جدید با API v4 قابل انجام نیست:

1. ابتدا Contract change نوشته و review شود.
2. backward compatibility مشخص شود.
3. Server و Agent هرکدام جداگانه تست و نسخه‌گذاری شوند.
4. `minimum_agent_version` / `recommended_agent_version` به‌صورت کنترل‌شده تغییر کند.
5. هیچ breaking change خام و بدون migration روی endpoint موجود منتشر نشود.

## تحویل نهایی برنامه‌نویس کافه به مالک

- diff سورس Server
- migrationهای DB در صورت وجود
- تست‌های contract/state/idempotency
- توضیح تغییرات admin/monitoring
- نتیجه تست staging
- فهرست هر تغییری که نیازمند نسخه جدید Agent است

اگر مورد آخر خالی باشد، Update سامانه کافه نباید نیازمند rebuild یا reinstall Agent باشد.
