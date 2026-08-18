# امنیت Print Agent v4

## Server/API
- Production فقط HTTPS. `X-Forwarded-Proto` تنها زمانی Trust می‌شود که `app.trust_proxy_headers` صریحاً فعال باشد؛ Client مستقیم نمی‌تواند با Header جعلی HTTPS check را دور بزند.
- Agent Token فقط هنگام ساخت نمایش داده می‌شود؛ Server Hash آن را نگه می‌دارد.
- Rotation و Revocation از مدل Agent موجود انجام می‌شود.
- Agent فقط Destinationهای مجاز/Primary خودش را در v4 می‌بیند.
- Payload size محدود و Payload snapshot قبل از Claim Hash شده است.
- تمام Queryهای این مسیر Prepared هستند.
- Transitionهای State Machine Server-side enforce می‌شوند؛ State ادعایی Agent Trust نمی‌شود.
- `request_id` برای mutationها idempotency boundary است؛ Attempt/receipt/hash نیز binding مستقل دارند.

## Windows
- Token با DPAPI `LocalMachine` در `secret.dat` ذخیره می‌شود.
- `%ProgramData%\Sokna\PrintAgent` فقط SYSTEM و Administrators Full Control دارد.
- Program Files برای SYSTEM read/execute و Administrator full کنترل می‌شود.
- Control App Token را نمایش یا Log نمی‌کند.
- Service به Session/UI کاربر وابسته نیست.

## Worker isolation
- Worker executable/path ثابت است و از Payload shell command ساخته نمی‌شود.
- `UseShellExecute=false` است.
- Service Worker را قبل از Start Signal به Windows Job Object با `KILL_ON_JOB_CLOSE` متصل می‌کند.
- Worker بدون Start Signal حق نزدیک‌شدن به Spooler ندارد.
- Queue name به Winspool API داده می‌شود و shell interpolation ندارد.

## Logging
Log عادی نباید شامل این موارد باشد:
- Authorization header یا Token خام
- Lease token خام
- Payload کامل سفارش
- داده حساس مشتری

Error messageها bounded و Human/technical detail از هم جدا هستند.

## Payload integrity
- Server `content_sha256` را از snapshot immutable تولید می‌کند.
- Agent قبل از Accept Hash را Verify می‌کند.
- Worker قبل از چاپ دوباره Hash را Verify می‌کند.
- Worker result نیز با job/attempt/local receipt/hash به SQLite ownership bind می‌شود.

## Proxy و Deployment
اگر Sokna پشت Reverse Proxy است، `trust_proxy_headers` فقط وقتی فعال شود که درخواست مستقیم Client به PHP از Proxy قابل دورزدن نباشد. در غیر این صورت Headerهای Forwarded امنیتی محسوب نمی‌شوند.
