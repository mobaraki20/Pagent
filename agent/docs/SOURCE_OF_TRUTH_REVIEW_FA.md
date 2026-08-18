# Source of Truth — Sokna Print Agent 6 / Print API v4

## Web/API baseline رسمی این مأموریت
- Sokna Web/API: `1.33.0-rc1`
- Migration: `migrations/1.33.0-print-agent-v4.sql`
- Print Protocol: `v4`
- Legacy Print API: فعلاً backward-compatible
- Agent target: `6.0.0`

Baselineهای `1.32.x` و `1.30.x` فقط History/Comparison هستند و Implementation Baseline محسوب نمی‌شوند. `SOKNA_PROJECT_HANDOFF.md` نیز قرارداد و تاریخچه است، نه اثبات پیاده‌سازی.

## Agent Source of Truth
Source فعال Agent فقط normal source tree زیر `agent/` در همین Repository است.

Release workflow:
- مستقیماً `agent/` را Restore/Build/Test/Publish می‌کند.
- `agent6.zip` را Extract نمی‌کند.
- `ci/patch_agent_source.py` را هنگام Build اجرا نمی‌کند.
- Source artifact را از Git tracked tree با `git archive HEAD:agent` می‌سازد.

بنابراین تغییر Production باید در فایل واقعی زیر `agent/` انجام شود؛ mutation هنگام CI یا patch stacking مجاز نیست.

## Packaging contract
سه self-contained component عمداً جدا هستند:
- `%ProgramFiles%\Sokna\PrintAgent\Service\`
- `%ProgramFiles%\Sokna\PrintAgent\Worker\`
- `%ProgramFiles%\Sokna\PrintAgent\Control\`

هم‌نام بودن بعضی Runtime DLLها بین Componentها مجاز است؛ flatten/overwrite آنها ممنوع است و Collision Guard این موضوع را کنترل می‌کند.

Mutable state فقط زیر `%ProgramData%\Sokna\PrintAgent` است و Upgrade نباید config/secret/SQLite/logs را حذف کند.

## وضعیت اثبات‌شده
Windows Build + Unit + NuGet vulnerability audit + Setup/Service/Uninstall smoke اجرا و PASS شده‌اند. این موضوع به معنی Production-ready بودن چاپ فیزیکی نیست؛ Printer/Spooler/Paper-Out/Windows-restart/soak و چاپ فارسی واقعی همچنان Production Gate جدا هستند.
