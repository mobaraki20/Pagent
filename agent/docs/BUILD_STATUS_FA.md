# وضعیت Build — Sokna Print Agent 6.0.0

## انجام‌شده
- Source کامل Core/Service/Worker/Control/Setup موجود است.
- `global.json` SDK را روی `10.0.302` pin می‌کند.
- مسیر Windows build: `scripts/Build-Agent.ps1`.
- مسیر Linux cross-build: `scripts/build-agent.sh`.
- هر دو مسیر Restore/Build/Test، runtime restore، win-x64 self-contained publish، collision guard، manifest/SHA و embedded `Setup.exe` را enforce می‌کنند.
- `Setup.exe` با manifest `requireAdministrator` ساخته می‌شود و package دارای SHA را در temp استخراج کرده و Installer staged را اجرا می‌کند.
- Windows CI workflow در ریشه repository وجود دارد و Setup واقعی + Service install/uninstall smoke را اجرا می‌کند.

## نتیجه Runtime فعلی
- `dotnet` نصب نیست.
- `scripts/bootstrap-dotnet10-local.sh` واقعاً اجرا شد.
- دانلود SDK با `curl: (6) Could not resolve host: dot.net` متوقف شد.
- بنابراین Compile C#، `dotnet test/run`، win-x64 publish و تولید PE واقعی در این Runtime **NOT RUN** است.

## نتیجه Release
Source/Build pipeline قابل Audit است، اما Binary قابل‌نصب فقط بعد از اجرای pipeline روی محیط دارای .NET 10 ساخته می‌شود. تا آن زمان:

**Agent binary build: PENDING — PRODUCTION GATE**

هیچ `Setup.exe` ساختگی یا Binary قدیمی به‌عنوان Agent 6 تحویل داده نمی‌شود.
