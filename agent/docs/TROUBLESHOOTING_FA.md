# عیب‌یابی Print Agent 6

- **Job blocked**: Mapping مقصد/Agent/Queue را در پنل اصلاح کنید؛ Job حذف نشده است.
- **Agent Offline**: Service، اینترنت و Token را بررسی کنید. Job required در MySQL می‌ماند.
- **Printer Offline/Paper Out**: Agent Claim جدید برای آن Queue نمی‌گیرد؛ Job Server باقی می‌ماند.
- **unknown/recovery_hold**: Retry ساده نزنید. ابتدا وضعیت کاغذ/Spooler را بررسی و یکی از دو Resolution مدیریتی را انتخاب کنید.
- **submitted**: فقط پذیرش Spooler است، نه اثبات خروج کاغذ.
- **SQLite locked**: Service با busy_timeout کار می‌کند؛ Control App نباید DB را مستقیم بازنویسی کند. تداوم Lock نیازمند بررسی Storage/AV است.
- **Config خراب**: Service چاپ جدید را متوقف می‌کند؛ Job Server از بین نمی‌رود. Config را با Control App اصلاح کنید.
- **Token**: Token خام Log نمی‌شود. در نشت احتمالی از پنل Rotate کنید.
