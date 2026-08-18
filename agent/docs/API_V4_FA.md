# قرارداد کامل Print API v4

Endpoint پایه:
`POST /print-agent/v4/api.php?action=<action>`

Header:
`Authorization: Bearer <agent-token>`

Production فقط HTTPS. Token خام Log/DB نمی‌شود. Protocol=4 و Agent 6.0.0 مبنای این قرارداد است.

## قواعد عمومی
- Mutationها `request_id` یکتای ۸ تا ۸۰ کاراکتر دارند.
- Replay همان request باید نتیجه منطقی قبلی را برگرداند و state جدید/چاپ جدید نسازد.
- Errorهای transition می‌توانند `current_state`, `terminal`, `requires_human_resolution` برگردانند.
- `submitted` هیچ‌وقت به معنی چاپ فیزیکی قطعی نیست.

## probe
Request:
```json
{"agent_version":"6.0.0","protocol_version":4}
```
Response نمونه:
```json
{
  "success":true,
  "protocol_version":4,
  "minimum_agent_version":"6.0.0",
  "recommended_agent_version":"6.0.0",
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

## heartbeat
تقریباً هر ۱۵ ثانیه:
```json
{
  "request_id":"h-...",
  "agent_version":"6.0.0",
  "protocol_version":4,
  "hostname":"CAFE-PC",
  "os_version":"Microsoft Windows ...",
  "uptime_seconds":3600,
  "last_poll_success_at":"2026-08-17T19:30:00Z",
  "local_backlog_count":2,
  "local_unknown_count":0,
  "last_submission_at":"2026-08-17T19:29:58Z",
  "sqlite_health":"ok",
  "disk_free_mb":20480,
  "worker_ok":true,
  "config_ok":true,
  "instance_lock_ok":true,
  "printers":[{
    "name":"Sokna-Bar-80","offline":false,"paused":false,
    "paper_out":false,"error":false,"jobs":0,"driver":"...","port":"IP_..."
  }]
}
```
Heartbeat ownership یا Failover ایجاد نمی‌کند.

## claim
```json
{
  "request_id":"c-...","agent_version":"6.0.0","protocol_version":4,
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

## accept
Agent قبل از این درخواست باید Claim را در SQLite Transaction ذخیره و SHA را Verify کرده باشد.
```json
{
  "request_id":"a-...","agent_version":"6.0.0","protocol_version":4,
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
  "request_id":"n-...","agent_version":"6.0.0","protocol_version":4,
  "attempt_id":91,"lease_token":"..."
}
```
Renew هرگز مجوز چاپ نیست.

## start
Service فقط برای local state `claimed` درخواست می‌دهد:
```json
{
  "request_id":"s-...","agent_version":"6.0.0","protocol_version":4,
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
  "request_id":"r-...","agent_version":"6.0.0","protocol_version":4,
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
