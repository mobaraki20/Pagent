# Sokna Print Agent 6.2.3 — Release Candidate Notes

نسخه 6.2.3 remediation مربوط به Heartbeat contract، Claim reconciliation، health separation و diagnostics است.

- Optional `null`های Heartbeat از wire حذف می‌شوند؛ Server compatibility با explicit null در A53 واقعی بررسی می‌شود.
- Claim collision دیگر loop exception/replay نیست؛ به quarantine پایدار و fail-closed تبدیل شده است.
- Transport / Coordinator / Printer Discovery health مستقل‌اند.
- Support Package sanitization تقویت شده است.
- A53 Real Heartbeat Contract اضافه شده است.

این source candidate تا قبل از Windows build/install/upgrade gate، A53/B53 واقعی و UAT سخت‌افزاری، Release نهایی یا Production-ready محسوب نمی‌شود.
