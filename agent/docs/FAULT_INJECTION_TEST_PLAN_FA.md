# برنامه Fault Injection Print Agent 6

## خودکار و قابل اجرا بدون Printer واقعی
- Claim replay / concurrent claim model
- Lease expiry قبل از Accept
- Accept تکراری با همان receipt و conflict با receipt متفاوت
- Start replay فقط در state started
- Start terminal-state fence
- Report تکراری / terminal conflict / late evidence
- payload hash tamper
- safe failure قبل از fence
- crash model بعد از fence → ambiguity
- report-outbox retry بدون reprint
- SQLite attempt identity و WAL/FULL contract
- load simulation: 25/min × 30min، burst 100، mixed 2000
- FIFO per destination

## Windows/Adapter آزمایشی
- Service restart قبل/بعد Accept
- Worker crash قبل Fence
- Worker crash بعد Fence
- Service kill بعد Start و قبل Worker
- Service kill حین Worker
- report network failure
- SQLite locked
- Config خراب
- Control App بسته/Hang
- دو Service instance

## Printer/Spooler Production Gate
- Spooler stop/start
- Queue حذف‌شده
- Printer Offline
- Paper Out
- Driver hang/timeout
- 50 receipt پشت‌سرهم
- فارسی/RTL، 58/80mm، bar/kitchen/customer
- Windows restart
- network disconnect/reconnect
- 24h soak؛ ترجیحاً 72h

هر سناریو باید تعداد Source Jobs، Attempts، Spooler submissions، unknownها، reprintهای انسانی و duplicateهای خودکار را ثبت کند. معیار کلیدی: **zero silent loss + zero automatic duplicate**.
