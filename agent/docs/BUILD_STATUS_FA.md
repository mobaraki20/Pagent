# وضعیت Build — Sokna Print Agent 6.0.0

## Source of Truth
- Source فعال Agent به‌صورت normal source tree در `agent/` نگهداری می‌شود.
- Release workflow مستقیماً از `agent/` Build می‌کند و به `agent6.zip` یا build-time patch وابسته نیست.
- Source artifact با `git archive HEAD:agent` فقط از فایل‌های tracked ساخته می‌شود.

## Windows Gate اجراشده
Run رسمی Windows پس از اصلاح Installer:
- .NET SDK 10.0.303: **PASS**
- Restore: **PASS**
- dependency graph: **PASS**
- NuGet vulnerability audit: **PASS — no vulnerable packages reported by current sources**
- `SQLitePCLRaw.bundle_e_sqlite3`: resolved `2.1.12`
- C# Build: **PASS — 0 warning / 0 error**
- `Sokna.PrintAgent.Tests`: **PASS**
- self-contained publish Service/Worker/Control: **PASS**
- collision guard with isolated component directories: **PASS**
- Setup publish: **PASS**
- `Setup.exe /quiet`: **PASS**
- Service install/start/fresh health: **PASS**
- isolated Service/Worker/Control layout: **PASS**
- Automatic + DelayedAutoStart: **PASS**
- Service Recovery configuration: **PASS**
- Control App independence: **PASS**
- Uninstall + ProgramData preservation: **PASS**

## Root Cause بسته‌شده Setup Exit 1
Failure قبلی در stage `payload_manifest_hash_validation` رخ می‌داد، چون Windows PowerShell child host مورد استفاده Setup در آن runner فرمان `Get-FileHash` را resolve نمی‌کرد. SHA-256 validation حذف یا suppress نشد؛ Installer اکنون مستقیماً از `System.Security.Cryptography.SHA256` استفاده می‌کند. Setup و Installer نیز stage/reference-id/child-exit/stdout/stderr sanitized ثبت می‌کنند.

## Artifact
Release workflow موارد زیر را تولید می‌کند:
- `Sokna-Print-Agent-6.0.0-Setup.exe`
- `Sokna-Print-Agent-6.0.0-win-x64.zip`
- `Sokna-Print-Agent-6.0.0-source.zip`
- `SHA256SUMS-Agent-6.0.0.txt`
- `BUILD_ARTIFACTS-Agent-6.0.0.json`
- Windows smoke evidence

SHA هر Artifact وابسته به Source commit همان Run است و فقط SHA موجود در همان Run معتبر است.

## هنوز Production PASS نیست
موارد زیر همچنان **PENDING / UAT_REQUIRED — PRODUCTION GATE** هستند:
- Printer Queue واقعی machine-wide
- Winspool روی Printer واقعی
- چاپ فارسی/RTL روی کاغذ 80mm و در صورت قرارداد 58mm
- Kitchen / Bar / Customer receipt
- Paper Out / Printer Offline / Queue deletion
- Spooler stop/start
- Windows restart
- internet loss/recovery در محیط واقعی
- 50 چاپ پشت سرهم
- Upgrade preservation روی سیستم عملیاتی واقعی
- 24h soak و ترجیحاً 72h soak

Windows CI PASS به‌تنهایی به معنی Exactly-once physical printing یا Production-ready نیست.
