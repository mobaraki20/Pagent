# State Machine نهایی Print API v4

## Job State سرور
| وضعیت | معنی | چاپ خودکار بعدی |
|---|---|---|
| `pending` | آماده Lease، mapping معتبر | مجاز |
| `blocked` | mapping/config لازم ناقص | ممنوع تا رفع block |
| `reserved` | Lease موقت؛ Agent هنوز مجوز چاپ ندارد | پس از expiry می‌تواند به pending برگردد |
| `claimed` | Agent Payload را Durable ذخیره و Accept کرده | فقط همان Attempt/Agent |
| `submitted` | Windows Spooler درخواست را پذیرفته | ممنوع |
| `failed` | خطای اثبات‌شده پیش از Submission | Retry محدود یا Retry دستی ایمن |
| `unknown` | احتمال Submission وجود دارد ولی نتیجه قطعی نیست | ممنوع |
| `recovery_hold` | Recovery خودکار ممکن است Duplicate ایجاد کند | ممنوع |
| `cancelled` | لغو Audit شده | ممنوع |

Attempt داخلی یک state اضافی `started` دارد: Server مجوز ورود Agent به اجرای PrintWorker را داده است؛ هنوز به معنی Spooler submission نیست.

## انتقال‌های اصلی
`pending → reserved → claimed → started → submitted`

مسیرهای Failure:
- `reserved → expired → pending` (فقط expiry امن پیش از Accept)
- `started → failed → pending` فقط وقتی Agent صریحاً ثابت کند Failure پیش از Submission Fence/Spooler بوده و سقف Auto-Retry رد نشده است.
- `started → unknown`
- `started → recovery_hold`
- مدیریت می‌تواند state مبهم را Resolve کند یا Reprint مستقل بسازد.

## Start authorization invariant
فقط دو حالت Start معتبرند:
1. Attempt در state `claimed` است → انتقال یک‌باره به `started`.
2. Attempt از قبل `started` است → replay idempotent همان مجوز.

هیچ state terminal (`submitted/failed/unknown/recovery_hold/cancelled/expired`) پاسخ موفق Start نمی‌گیرد. این قاعده جلوی چاپ دوباره توسط local claim قدیمی بعد از Resolution مدیریتی را می‌گیرد.

## Reserved
Lease منقضی `reserved` چون Agent هنوز Accept نکرده می‌تواند ایمن آزاد شود. Auto reservation retry محدود است؛ بعد از سقف، Exception مدیریتی ایجاد می‌شود. `attempt_count` تاریخچه monotonic است و برای Retry دستی reset نمی‌شود.

## Claimed و Started
Heartbeat loss هیچ‌کدام را به Agent دیگر واگذار نمی‌کند. اگر مدیر stale claimed را Hold کند:
- claimed پیش از start → `recovery_hold`
- started → `unknown`
Agent در Start بعدی state terminal را می‌بیند و Local ownership را بدون چاپ Resolve می‌کند.

## submitted
`submitted` فقط یعنی Windows `StartDoc/EndDoc` را پذیرفته و Spooler Job ID داریم. «چاپ فیزیکی تأیید شد» گفته نمی‌شود.

## unknown / recovery_hold
Retry ساده ندارند. دو Resolution مدیریتی:
1. «چاپ انجام شده است» → Resolve/Audit.
2. «چاپ مجدد» → **Print Job جدید** با `reprint_of_id`, reason, user/time و نشان درشت `چاپ مجدد`.

Job قبلی هرگز Reset نمی‌شود.

## FIFO Fence
برای هر `destination_key`، Job قدیمی‌تر حل‌نشده Fence است. Job جدیدتر از pending/blocked/failed/claimed/unknown/recovery_hold مرتبط عبور نمی‌کند مگر Override مجاز و Audit‌شده. هدف حفظ ترتیب فیش‌های یک مقصد و جلوگیری از دورزدن Exception است.
