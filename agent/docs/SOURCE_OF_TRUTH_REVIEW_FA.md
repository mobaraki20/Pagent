# گزارش Source of Truth ورودی Print Agent v4

## فایل‌های ورودی
- `Sokna-1.32.20-RC1-clean-install-full.zip`
- `Sokna-1.32.20-RC1-release-bundle.zip`

## نتیجه مقایسه
Clean Install شامل Source اجرایی کامل Sokna است. Release Bundle ظرف انتشار است و همان Full ZIP را byte-for-byte به‌همراه Update package، SHA256، Test Report، Release Record، Changelog و Go-Live metadata حمل می‌کند.

**Source of Truth کد پایه:** محتوای Clean Install Full `1.32.20-rc1`.

**Source of Truth قرارداد Artifact انتشار:** Release Bundle و اسناد Published آن.

هیچ Binary Agent قدیمی Patch نشده است. `Sokna-Print-Agent-5.0.0-Setup.exe` فقط به‌عنوان Legacy artifact باقی مانده و Agent 6 در `print-agent-v6/` Source مستقل Clean-slate دارد.

## مرز نسخه جدید
- Sokna Web/Server candidate: `1.33.0-rc2`
- Print Agent source: `6.0.0`
- Print API: `v4`
- Print document contract: `sokna-print-document-v2`, `contract_version=4`
