# وابستگی‌های Server برای Remediation Agent

این سند مرز بین آزمون‌های مستقل Agent و آزمون‌هایی را که به Print API واقعی نیاز دارند روشن می‌کند. هیچ مقدار credential، token یا secret نباید در مخزن، log یا result JSON ثبت شود.

## A49 — قرارداد واقعی Print API v4

A49 فقط با `tests/Sokna.PrintAgent.RealApiIntegration` اجرا می‌شود و از `HttpPrintTransport` production استفاده می‌کند. Loopback/fake HTTP برای PASS این Case مجاز نیست.

پیش‌نیازهای اجباری:

- `SOKNA_ACCEPTANCE_SERVER_URL`: URL مستقیم یک محیط acceptance مجزا؛ Production عملیاتی مجاز نیست.
- `SOKNA_ACCEPTANCE_TOKEN_FILE`: مسیر فایل محلی token همان محیط. محتوای token هرگز در evidence نوشته نمی‌شود.
- `SOKNA_ACCEPTANCE_DESTINATION_KEY`: مقصدی که یک job disposable برای آن از قبل seed شده باشد.
- `SOKNA_ACCEPTANCE_FAULT_PROXY_URL`: proxy کنترل‌شده‌ای که به همان server instance upstream می‌رود.
- `SOKNA_ACCEPTANCE_ALLOW_MUTATION=I_UNDERSTAND_THIS_MUTATES_ACCEPTANCE_API`: opt-in صریح برای mutation محیط acceptance.

### قرارداد Fault Proxy

برای اثبات «response lost after commit»، proxy باید وقتی header زیر روی درخواست وجود دارد:

`X-Sokna-Acceptance-Drop-Response: after-commit`

درخواست را کامل به upstream ارسال کند، اجازه دهد upstream آن را commit/پردازش کند، سپس پاسخ برگشتی را قبل از تحویل به client قطع کند. قطع اتصال قبل از forward یا قبل از commit، evidence معتبر A49 نیست. Harness پس از خطای transport از endpoint مستقیم `attempt_status` را می‌خواند؛ تنها وقتی همان attempt/receipt به وضعیت پذیرفته‌شده bind شده باشد، lost-response recovery اثبات می‌شود.

Proxy باید بدون این header transparent باشد. Probe مستقیم و Probe از proxy، در صورت وجود `server_instance_id`، باید همان identity را گزارش کنند.

## عملیات کنترل‌شده A49

Harness کارهای زیر را روی job disposable انجام می‌دهد:

1. Probe و capability check (`attempt_status`).
2. Claim برای destination مشخص.
3. Accept از fault proxy با قطع پاسخ پس از commit.
4. AttemptStatus مستقیم برای اثبات commit همان attempt و receipt.
5. Replay دقیق همان Accept request id/body از مسیر مستقیم برای idempotency.
6. Start همان attempt از مسیر مستقیم.
7. AttemptStatus بعد از Start.
8. Report کنترل‌شده با status=`failed` و بدون اجرای Worker یا Spooler محلی.
9. AttemptStatus نهایی برای اثبات مسیر non-print/terminal بعد از Report.

A49 هیچ Worker محلی را اجرا نمی‌کند و هیچ submission به Windows Spooler انجام نمی‌دهد؛ با این حال Claim/Accept/Start/Report روی محیط server state را تغییر می‌دهند، به همین دلیل opt-in mutation الزامی است.

اگر هرکدام از تنظیمات بالا، job seed شده، proxy واقعی یا capability لازم موجود نباشد، A49 باید `NOT_RUN` باقی بماند. fixture یا mock نباید جای آن را PASS کند.

## فرمان اجرا

از ریشه `agent/`:

```powershell
./scripts/Test-Agent-Acceptance.ps1 -Suite Integration -ResultsDirectory ./artifacts/integration
```

یا برای همان Case:

```powershell
./scripts/Test-Agent-Acceptance.ps1 -CaseId A49 -ResultsDirectory ./artifacts/A49
```

Evidence معتبر باید source SHA، زمان اجرا، environment، exit code، assertionها، raw log، server instance (اگر API ارائه کند) و request idهای غیرمحرمانه را نگه دارد؛ token و lease token نباید ثبت شوند.

## مرز تأیید

سبز بودن CI معمول Agent، Loopback tests، A25–A29 یا Windows installer به معنی `Integration verified` نیست. این سطح فقط پس از PASS واقعی A49 روی محیط acceptance تعریف‌شده و بررسی evidence قابل ارتقا است. UAT فیزیکی چاپگر نیز جداست و تا اجرای Caseهای سخت‌افزاری مربوط، `Operationally accepted=false` باقی می‌ماند.
