# Build واقعی Windows برای Sokna Print Agent 6

Source of Truth فقط normal source tree زیر `agent/` است. Workflow رسمی در `.github/workflows/build-agent.yml` روی `windows-latest` اجرا می‌شود و SDK مورد استفاده را از `agent/global.json` می‌گیرد؛ Runهای تأییدشده فعلی با .NET SDK `10.0.303` اجرا شده‌اند.

Pipeline فقط Compile نیست. Gateهای اصلی:

1. Verify اینکه `agent/` موجود است و `agent6.zip` / `ci/patch_agent_source.py` دوباره وارد مسیر Build نشده‌اند.
2. Restore و dependency graph + NuGet vulnerability audit بدون suppress کردن Security warning.
3. Build با TreatWarningsAsErrors و اجرای `Sokna.PrintAgent.Tests`.
4. Publish مستقل Service، Worker و Control به‌صورت self-contained `win-x64`.
5. Collision guard برای جلوگیری از flatten/overwrite Runtime DLLهای هم‌نام.
6. ساخت Payload manifest با size/SHA-256 و Setup.exe تک‌فایلی با Payload embedded.
7. اجرای خود `Setup.exe /quiet` روی Windows runner.
8. تأیید Service `SoknaPrintAgent6`: Running، مسیر binary ایزوله، Automatic Delayed Start، Recovery Restart، health تازه و استقلال از Control App.
9. Uninstall واقعی و اثبات حذف Service/Program Files و حفظ ProgramData به‌صورت پیش‌فرض.
10. ساخت Source artifact فقط از tracked tree با `git archive HEAD:agent`.
11. Verify Artifactهای اجباری و Upload Setup/Package/Source/SHA/Build metadata/Windows evidence.

Failure نصب باید Job را قرمز نگه دارد؛ Evidence collection مجاز است اما نباید Release Gate را سبز کند.

این Pipeline Gate چاپگر فیزیکی نیست. Printer Queue واقعی، فارسی روی کاغذ، Paper Out، قطع Spooler، Windows restart، internet loss، upgrade preservation و soak همچنان `PENDING / UAT_REQUIRED — PRODUCTION GATE` باقی می‌مانند.
