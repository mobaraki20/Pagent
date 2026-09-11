# Sokna Print Agent 6.x — Platform Support

## معماری پردازنده

Sokna Print Agent 6.x یک محصول **Windows x64-only** است.

Artifact رسمی قابل انتشار:

- `Sokna-Print-Agent-{version}-Setup.exe` برای Windows x64
- `Sokna-Print-Agent-{version}-win-x64.zip`

Runtime prerequisite نیز فقط `Microsoft Windows Desktop Runtime 10.x x64` است.

## معماری‌های خارج از پشتیبانی

موارد زیر در Agent 6.x build/release نمی‌شوند و تست یا UAT رسمی ندارند:

- `win-x86` / Windows 32-bit
- `win-arm64`
- سایر RuntimeIdentifierها

`Directory.Build.targets` یک build guard دارد که اگر پروژه‌های Agent با RuntimeIdentifier غیر از `win-x64` ساخته شوند، build را fail می‌کند. اضافه‌کردن معماری جدید در آینده نیازمند تصمیم محصول، installer/runtime bootstrap مستقل، CI و UAT کامل همان معماری است؛ نباید صرفاً با تغییر RID منتشر شود.

## دلیل تصمیم

هدف، کاهش matrix تست و release، جلوگیری از artifact اشتباه و تمرکز روی معماری واقعی سیستم‌های صندوق مورد استفاده Sokna است. این تصمیم به معنی حذف یک package فعال 32-bit نیست؛ خط انتشار فعلی از قبل فقط `win-x64` تولید می‌کرد و اکنون این محدودیت به قرارداد صریح محصول تبدیل شده است.
