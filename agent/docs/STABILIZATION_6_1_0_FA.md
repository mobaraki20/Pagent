# Sokna Print Agent 6.1.0 — Stabilization & Productization

این نسخه Root Causeهای کشف‌شده در UAT ویندوز را بدون افزودن Subsystem یا مسیر Deploy موازی اصلاح می‌کند. معماری Protected همان است:

`Sokna PHP/MySQL -> Print API v4 -> Windows Service -> SQLite durable queue -> isolated Worker -> Winspool -> Printer`

## 1) Transport reliability

- timeout فعلی HTTP همچنان bounded است؛ راه‌حل افزایش کور Timeout نیست.
- هر Call با نام Action واقعی Log می‌شود؛ عبارت مبهم `poll` دیگر Owner خطای transport نیست.
- ترتیب Loop عملیاتی: `Accept reserved -> Flush durable reports -> Process claimed -> Claim new -> Heartbeat -> Refresh destinations`.
- Heartbeat و destination refresh fail-soft هستند و حق starvation مسیر چاپ را ندارند.
- `HttpClient` candidate در configuration failure Dispose می‌شود.

## 2) Durable claim replay

قبل از `claim`، envelope شامل `request_id`, `ready_destination_keys`, `limit`, `created_at` داخل `agent_meta` SQLite پایدار می‌شود. اگر HTTP response گم شود یا Service restart شود، همان envelope replay می‌شود. Metadata فقط بعد از persist شدن کل response در SQLite پاک می‌شود.

این رفتار از claim ledger موجود Server استفاده می‌کند و Queue/Broker جدیدی معرفی نمی‌کند.

## 3) Health evidence

Local health و Heartbeat می‌توانند این Evidence اختیاری را گزارش کنند:

- `last_successful_action`
- `last_api_success_at`
- `last_api_error_code`
- `consecutive_api_failures`
- `last_api_latency_ms`

این داده‌ها diagnostic هستند و هیچ ownership/state/failover ایجاد نمی‌کنند. Token/Secret/Lease در Health/Support Package ممنوع است.

## 4) Operations & Diagnostics Console

Control App از Form آزمایشی قبلی به WPF Console چندبخشی تبدیل شده است:

- Overview و health exception-first
- Printer/Queue visibility زیر LocalSystem
- Test Center برای Service/Config/Credential/Spooler/Printer/API
- Log viewer برای Agent و Setup diagnostics
- Event Viewer access
- Support Package redacted
- Settings فقط برای Server URL و Credential rotation

End-to-End Test Print عمداً Winspool را مستقیم دور نمی‌زند؛ از Test Print موجود در Sokna Server استفاده می‌شود.

## 5) Installer / Upgrade

یک `Setup.exe` و یک PowerShell engine باقی می‌ماند. UI Setup فقط همان stageهای واقعی engine را نمایش می‌دهد:

- payload extraction/manifest/SHA-256
- ProgramData/ACL
- staged binary swap
- Service Automatic Delayed Start + Recovery
- health validation
- Start Menu/Desktop shortcut
- finalize یا rollback

Upgrade باید `config.json`, `secret.dat`, `queue.db`, logs و work state را حفظ کند. Uninstall پیش‌فرض ProgramData را حذف نمی‌کند و shortcutها را پاک می‌کند.

## 6) Version owner

`Directory.Build.props:SoknaAgentVersion` تنها Version owner است. Build artifacts، Setup filename و Assembly metadata از آن مشتق می‌شوند. Runtime transport از Assembly version جاری استفاده می‌کند.

## 7) Gate

Done فقط بعد از موارد زیر:

- restore/build بدون warning/error
- vulnerability audit
- unit tests شامل `agent_meta` persistence/restart/clear
- Setup `/quiet` روی Windows runner
- Service start + fresh health
- Start Menu/Desktop shortcuts create/remove
- default uninstall preserves ProgramData
- UAT واقعی Printer/RTL/Paper Out/Spooler/network/restart/50 prints/soak

CI سبز به‌تنهایی Production Ready نیست. Physical UAT همچنان `UAT_REQUIRED` است.
