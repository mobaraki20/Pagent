# قرارداد کامل Print API v4

Endpoint پایه:
`POST /print-agent/v4/api.php?action=<action>`

Header:
`Authorization: Bearer <agent-token>`

Production فقط HTTPS. Token خام Log/DB نمی‌شود. Protocol=4 و Agent 6.0.0 baseline اولیه این قرارداد است؛ Agent 6.1.0 بدون breaking change روی همین Protocol v4 کار می‌کند و فقط فیلدهای diagnostic اختیاری heartbeat را توسعه می‌دهد.

## قواعد عمومی
- Mutationها `request_id` یکتای ۸ تا ۸۰ کاراکتر دارند.
- Replay همان request باید نتیجه منطقی قبلی را برگرداند و state جدید/چاپ جدید نسازد.
- Errorهای transition می‌توانند `current_state`, `terminal`, `requires_human_resolution` برگردانند.
- `submitted` هیچ‌وقت به معنی چاپ فیزیکی قطعی نیست.
- افزودن فیلد diagnostic اختیاری به heartbeat در Protocol v4 breaking change نیست؛ Server باید Agentهای 6.0 فاقد این فیلدها را نیز بپذیرد.

## probe
Request:
```json
{"agent_version":"6.1.0","protocol_version":4}
```
Response نمونه:
```json
{
  "success":true,
  "protocol_version":4,
  "minimum_agent_version":"6.0.0",
  "recommended_agent_version":"6.1.0",
  "destinations":[{
    "destination_key":"bar",
    "label":"بار",
    "windows_queue_name":"Sokna-Bar-80",
    "paper_width_mm":80,
    "printable_width_mm":72,
    "copies":1,
    "layout_mode":"preparation"
  }]
}
```

`minimum_agent_version` و `recommended_agent_version` توسط Server تعیین می‌شوند. تا زمانی که migration رسمی انجام نشده، افزایش نسخه Agent نباید Protocol v4 یا compatibility با minimum اعلام‌شده را بشکند.

## heartbeat
تقریباً هر ۱۵ ثانیه:
```json
{
  "request_id":"h-...",
  "agent_version":"6.1.0",
  "protocol_version":4,
  "hostname":"CAFE-PC",
  "os_version":"Microsoft Windows ...",
  "uptime_seconds":3600,
  "last_poll_success_at":"2026-08-25T05:30:00Z",
  "local_backlog_count":2,
  "local_unknown_count":0,
  "last_submission_at":"2026-08-25T05:29:58Z",
  "sqlite_health":"ok",
  "disk_free_mb":20480,
  "worker_ok":true,
  "config_ok":true,
  "instance_lock_ok":true,
  "printers":[{
    "name":"Sokna-Bar-80","offline":false,"paused":false,
    "paper_out":false,"error":false,"jobs":0,"driver":"...","port":"IP_..."
  }],
  "last_successful_action":"claim",
  "last_api_success_at":"2026-08-25T05:30:01Z",
  "last_api_error_code":null,
  "consecutive_api_failures":0,
  "last_api_latency_ms":84
}
```

فیلدهای زیر extension اختیاری Agent 6.1 هستند و در Agent 6.0 ممکن است وجود نداشته باشند:
- `last_successful_action`
- `last_api_success_at`
- `last_api_error_code`
- `consecutive_api_failures`
- `last_api_latency_ms`

این فیلدها فقط Observability/Diagnostics هستند؛ ownership، failover، authorization چاپ یا state transition ایجاد نمی‌کنند. Heartbeat failure نیز نباید مسیر چاپ را block کند.

## claim
```json
{
  "request_id":"c-...","agent_version":"6.1.0","protocol_version":4,
  "limit":3,"ready_destination_keys":["bar","kitchen"]
}
```
`limit` Server-side bounded است (batch کوچک، حداکثر ۵).

Response هر item شامل:
- Job id/public token/type/required/entity/time
- `contract_version`
- immutable `payload_json`
- `content_sha256`
- Attempt id/no
- `lease_token`, `lease_expires_at`
- Destination snapshot: queue/paper/printable/copies/layout

Server state: `pending → reserved`.

### Claim replay در Agent 6.1
Agent باید envelope مربوط به Claim شامل `request_id`، `ready_destination_keys` و `limit` را پیش از ارسال به‌صورت durable ذخیره کند. اگر پاسخ Claim به‌علت timeout/restart نامشخص شد، همان `request_id` و همان body replay می‌شوند. Server باید همان نتیجه منطقی قبلی را برگرداند و attempt جدید نسازد. Envelope فقط بعد از durable شدن کامل پاسخ Claim در SQLite محلی پاک می‌شود.

## accept
Agent قبل از این درخواست باید Claim را در SQLite Transaction ذخیره و SHA را Verify کرده باشد.
```json
{
  "request_id":"a-...","agent_version":"6.1.0","protocol_version":4,
  "attempt_id":91,"lease_token":"...",
  "local_receipt_id":"r-...","content_sha256":"<64hex>"
}
```
موفق: `reserved → claimed`.
Replay با همان receipt idempotent است. Receipt متفاوت conflict است.

## renew
فقط `reserved` و قبل از expiry:
```json
{
  "request_id":"n-...","agent_version":"6.1.0","protocol_version":4,
  "attempt_id":91,"lease_token":"..."
}
```
Renew هرگز مجوز چاپ نیست.

## start
Service فقط برای local state `claimed` درخواست می‌دهد:
```json
{
  "request_id":"s-...","agent_version":"6.1.0","protocol_version":4,
  "attempt_id":91,"lease_token":"..."
}
```
Server فقط:
- `claimed → started`
- یا replay state `started`
را موفق می‌کند.

اگر state سرور terminal باشد:
```json
{
  "success":false,"code":"invalid_transition",
  "current_state":"recovery_hold",
  "terminal":true,"requires_human_resolution":true
}
```
Agent باید local ownership قدیمی را **بدون چاپ** متوقف کند.

پس از Start موفق، Service Worker را isolate می‌کند. **Worker پس از Render و بررسی هندسه، دقیقاً بلافاصله پیش از `StartDoc` در Winspool، Submission Fence محلی را Durable می‌نویسد.**

## report
### submitted
```json
{
  "request_id":"r-...","agent_version":"6.1.0","protocol_version":4,
  "attempt_id":91,"lease_token":"...","local_receipt_id":"r-...",
  "status":"submitted","spooler_job_id":"483","retryable":false
}
```
Response:
```json
{"success":true,"status":"submitted","physical_print_confirmed":false}
```

### safe failure قبل از submission
```json
{
  "status":"failed","retryable":true,
  "error_code":"printer_open_failed","error_message":"..."
}
```
فقط Failure قابل اثبات پیش از ambiguity می‌تواند Auto-Retry محدود بسازد.

### ambiguity
```json
{
  "status":"unknown","retryable":false,
  "spooler_job_id":"483","error_code":"spooler_ambiguity"
}
```
یا `recovery_hold`. هیچ Auto-Reprint ندارد.

### Late evidence
اگر attempt واقعاً `started` بوده، بعداً `unknown/recovery_hold` شده، هنوز Human-resolved نشده، و همان local receipt + spooler id evidence برسد، Server می‌تواند state را به `submitted` ارتقا دهد. این فقط ثبت evidence است و هیچ چاپی ایجاد نمی‌کند.

## امنیت idempotency
- Claim replay با همان `claim_request_id` attempt جدید نمی‌سازد.
- Accept به `local_receipt_id` و hash bind است.
- Start terminal state را هرگز دوباره authorize نمی‌کند.
- Report terminal conflict را رد می‌کند؛ late-submitted فقط طبق قاعده بالا پذیرفته می‌شود.
