# Build واقعی Windows برای Sokna Print Agent 6

این پروژه برای Build نهایی `win-x64` به .NET SDK **10.0.302** قفل شده است. Workflow رسمی پروژه در `../../.github/workflows/build-print-agent-v6.yml` روی `windows-latest` اجرا می‌شود.

Pipeline فقط Compile نیست. مراحل زیر Gate هستند:

1. نصب دقیق SDK 10.0.302 و تأیید `dotnet --version`.
2. Restore/Build و اجرای تست‌های Agent.
3. Publish مستقل Service، Worker و Control App به‌صورت self-contained `win-x64`.
4. ساخت Payload manifest با SHA-256.
5. ساخت Setup.exe تک‌فایلی با Payload embedded.
6. بررسی PE/MZ بودن Setup.exe و وجود Manifestهای Build.
7. اجرای خود **Setup.exe واقعی** روی Windows runner.
8. تأیید ایجاد و Running شدن `SoknaPrintAgent6`، تولید تازه `health.json`، وجود Service/Worker/Control، و Recovery action.
9. Uninstall واقعی و اثبات اینکه ProgramData به‌صورت پیش‌فرض حذف نمی‌شود.
10. Upload خروجی‌های EXE/ZIP/SHA/Build metadata به‌عنوان Artifact.

این Pipeline Gate چاپگر فیزیکی نیست. تست Queue واقعی، فارسی روی کاغذ، Paper Out، قطع Spooler، Restart ویندوز و Soak همچنان `PENDING — PRODUCTION GATE` باقی می‌مانند.
