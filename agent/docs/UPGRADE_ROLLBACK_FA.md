# Upgrade و Rollback عملیاتی Agent 5 → Agent 6

## اصل
Migration DB افزایشی است و history چاپ را حذف نمی‌کند. Rollback عملیاتی به معنی برگشت destination به Agent legacy است؛ **حذف ستون‌ها یا print_attempts در Rollback توصیه و خودکار نمی‌شود** چون Audit history را تخریب می‌کند.

## rollout تدریجی
1. Migration `1.33.0-print-agent-v4.sql` اجرا شود.
2. Agent 6 روی Windows نصب ولی مقصدها هنوز به آن assign نشوند.
3. API/heartbeat/queue health و Test Print بررسی شود.
4. یک destination کم‌ریسک به Agent 6 assign شود.
5. پس از assign، Agent protocol=4 می‌شود و Endpoint legacy برای همان مقصد دیگر fallback رقابتی نمی‌دهد.
6. سپس مقصدهای دیگر یکی‌یکی مهاجرت کنند.

## Rollback قبل از Claim
اگر destination هنوز Print Job `claimed/started/unknown/recovery_hold` v4 ندارد، Primary را به Agent 5 برگردانید. Jobهای `pending/blocked` می‌توانند بعد از mapping مجدد در مسیر legacy ادامه یابند.

## Rollback با Job پذیرفته‌شده v4
`claimed/started/unknown/recovery_hold` را به Agent 5 یا Printer دیگر auto-reroute نکنید. ابتدا در پنل تعیین تکلیف انسانی انجام شود. فقط Job اثباتاً چاپ‌نشده مجاز به reroute/reprint است.

## Schema rollback
Schema additive باقی می‌ماند. Downgrade کد به 1.32.20 با Schema اضافه باید قبل از Production به‌صورت staging تست شود؛ داده v4 حذف نمی‌شود. Destructive DROP فقط در محیط غیرتولیدی بدون history و با Backup مجاز است.
