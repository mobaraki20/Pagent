# Remediation چاپ Agent 6.2.3

مبنای authoritative این تغییر checkout رسمی GitHub در commit زیر است:

`5a58a10f6326698f80c3408a7a69229754e5c9d4`

فایل `Pagent-6.2.2.zip` با SHA-256 زیر فقط evidence تکمیلی است:

`9b8921552bdcec263b7e91e31db99a976d4567f24120cac0305c8052c0ec4491`

`Directory.Build.props` در baseline رسمی نسخه 6.2.2 را اعلام می‌کند. شاخه‌ی remediation مستقیماً از commit بالا ساخته شده است. ZIP فاقد `.git` است؛ بنابراین انتساب cryptographic خود ZIP به commit از داخل ZIP قابل اثبات نیست و این محدودیت جداگانه در `BASELINE_PROVENANCE.json` حفظ شده است.

## تغییرات

- Heartbeat wire به builder اختصاصی منتقل شد؛ optionalهای `null` روی wire حذف می‌شوند و global `AgentOptions.JsonOptions()` تغییر نکرده است.
- تمام diagnostics مدل Heartbeat، از جمله failure/freshness/generation مربوط به Printer Discovery، روی wire پوشش داده می‌شوند.
- `PrintApiException.Field` اضافه شد و `field` پاسخ API فقط بعد از allowlist ساده‌ی نام فیلد نگه‌داری می‌شود. body کامل پاسخ در exception/log تزریق نمی‌شود.
- `PersistReservedResultAsync` با dispositionهای `Created`, `ExactReplay`, `ReconciliationRequired` اضافه شد؛ mismatch فقط نام فیلدهای اختلاف را برمی‌گرداند و row موجود overwrite نمی‌شود.
- Claim pending state به envelope نسخه‌دار `pending_claim_state_v2` ارتقا یافت. metadata قدیمی `pending_claim_v1` در اولین بار به state جدید migrate می‌شود بدون ساخت request_id جدید.
- conflict به `quarantined` durable تبدیل می‌شود. در quarantine، Claim جدید برای همان coordinator متوقف می‌شود اما side-I/O مربوط به Heartbeat و Report قبل از coordinator همچنان اجرا می‌شود.
- مدل health به Transport / Coordinator / Printer Discovery تفکیک شد. موفقیت HTTP دیگر به تنهایی Coordinator conflict را سبز نمی‌کند.
- Control Console واژه‌های عملیاتی را از «پرینتر فیزیکی» به «صف چاپ Windows» تغییر داده و Coordinator را مستقل نمایش می‌دهد.
- Support Package همچنان `queue.db`, secret و token را صادر نمی‌کند؛ log/setup text قبل از ورود به ZIP دوباره sanitize می‌شود و summary فقط reconciliation metadata امن را اضافه می‌کند.
- Case جدید `A53` برای Real Heartbeat Contract اضافه شد. mock/loopback حق PASS کردن آن را ندارد.

## SQLite / Upgrade

Schema اصلی SQLite از نسخه 6.2.2 تغییری نمی‌کند (`schema_version=4`). Quarantine از `agent_meta` نسخه‌دار استفاده می‌کند، بنابراین migration ساختاری destructive لازم نیست. `local_jobs`, `attempt_outcomes`, `report_outbox`, config و request identityها حفظ می‌شوند.

## Rollback

- Downgrade binary به 6.2.2 داده‌های `local_jobs/report_outbox/outcomes` را حذف نمی‌کند.
- 6.2.2 کلید `pending_claim_state_v2` را نمی‌شناسد؛ بنابراین در صورت وجود quarantine، downgrade نباید برای ادامه‌ی خودکار Claim استفاده شود. قبل از downgrade باید reconciliation انسانی انجام و وضعیت مستند شود.
- حذف `queue.db` یا پاک‌کردن pending claim برای recovery ممنوع است.

## Gateهای باقی‌مانده

این محیط Linux فاقد SDK/.NET نصب‌شده و فاقد Windows Service/Spooler است. bootstrap رسمی SDK 10.0.302 نیز به دلیل DNS محیط (`Could not resolve host: dot.net`) اجرا نشد. بنابراین build/installer/upgrade Windows و A53 واقعی در این تحویل `NOT_RUN/BLOCKED_EXTERNAL` هستند و Production-ready اعلام نمی‌شود.
