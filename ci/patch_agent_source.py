from __future__ import annotations

import argparse
import re
from pathlib import Path


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")


def ensure_sqlite_security(root: Path) -> None:
    props = root / "Directory.Packages.props"
    s = read(props)
    if 'SQLitePCLRaw.bundle_e_sqlite3' not in s:
        marker = "  </ItemGroup>"
        assert marker in s
        s = s.replace(marker, '    <PackageVersion Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.12" />\n' + marker, 1)
    else:
        s = re.sub(r'(<PackageVersion Include="SQLitePCLRaw\.bundle_e_sqlite3" Version=")[^"]+("\s*/>)', r'\g<1>2.1.12\2', s)
    write(props, s)

    core = root / "src" / "Sokna.PrintAgent.Core" / "Sokna.PrintAgent.Core.csproj"
    s = read(core)
    if 'PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3"' not in s:
        marker = "</ItemGroup>"
        assert marker in s
        s = s.replace(marker, '  <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" />\n  ' + marker, 1)
    write(core, s)


def ensure_dotnet10_source_compat(root: Path) -> None:
    store = root / "src" / "Sokna.PrintAgent.Core" / "LocalQueueStore.cs"
    s = read(store)
    s = re.sub(
        r"await using var (\w+)\s*=\s*await (\w+)\.BeginTransactionAsync\(ct\);",
        r"await using var \1=(SqliteTransaction)await \2.BeginTransactionAsync(ct);",
        s,
    )
    write(store, s)

    control = root / "src" / "Sokna.PrintAgent.Control" / "Sokna.PrintAgent.Control.csproj"
    s = read(control).replace('Project Sdk="Microsoft.NET.Sdk.WindowsDesktop"', 'Project Sdk="Microsoft.NET.Sdk"')
    write(control, s)


def ensure_isolated_package(root: Path) -> None:
    build = root / "scripts" / "Build-Agent.ps1"
    s = read(build)
    if "$layout=[ordered]@{" not in s:
        start = s.find("# All executables deliberately share one install directory.")
        end = s.find("Copy-Item (Join-Path $root 'installer\\Install-SoknaPrintAgent.ps1') $package")
        assert start >= 0 and end > start, "obsolete flat payload block not found"
        replacement = r'''# Each self-contained process owns its runtime dependency set. Do not flatten these outputs:
# Service uses the base runtime while Worker/Control use WindowsDesktop; same-named framework DLLs may
# legitimately contain different bytes. Isolating each process prevents runtime assembly overwrite.
$layout=[ordered]@{
  'Sokna.PrintAgent.Service'='Service'
  'Sokna.PrintAgent.Worker'='Worker'
  'Sokna.PrintAgent.Control'='Control'
}
foreach($project in $layout.Keys){
  $src=Join-Path $Output $project
  $dest=Join-Path $payload $layout[$project]
  New-Item $dest -ItemType Directory -Force|Out-Null
  Copy-Item (Join-Path $src '*') $dest -Recurse -Force
}

'''
        s = s[:start] + replacement + s[end:]
    assert "collisionHashes" not in s, "flat collision guard still active"
    write(build, s)


def ensure_worker_path(root: Path) -> None:
    p = root / "src" / "Sokna.PrintAgent.Service" / "PrintAgentService.cs"
    s = read(p)
    old = 'var worker=Path.Combine(AppContext.BaseDirectory,"Sokna.PrintAgent.Worker.exe");'
    if old in s:
        s = s.replace(old, 'var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));')
    old = "WorkingDirectory=AppContext.BaseDirectory,RedirectStandardError=true"
    if old in s:
        s = s.replace(old, "WorkingDirectory=Path.GetDirectoryName(worker)!,RedirectStandardError=true")
    assert '"..","Worker","Sokna.PrintAgent.Worker.exe"' in s
    write(p, s)


def ensure_installer_layout(root: Path) -> None:
    p = root / "installer" / "Install-SoknaPrintAgent.ps1"
    s = read(p)
    pairs = [
        ("Join-Path $source 'Sokna.PrintAgent.Service.exe'", "Join-Path $source 'Service\\Sokna.PrintAgent.Service.exe'"),
        ("Join-Path $stage 'Sokna.PrintAgent.Service.exe'", "Join-Path $stage 'Service\\Sokna.PrintAgent.Service.exe'"),
        ("$exe=Join-Path $InstallRoot 'Sokna.PrintAgent.Service.exe'", "$exe=Join-Path $InstallRoot 'Service\\Sokna.PrintAgent.Service.exe'"),
        ("Open Sokna.PrintAgent.Control.exe as Administrator", "Open Control\\Sokna.PrintAgent.Control.exe as Administrator"),
    ]
    for old, new in pairs:
        if old in s:
            s = s.replace(old, new)

    old = """if($hadPrevious -and (Test-Path (Join-Path $InstallRoot 'Sokna.PrintAgent.Service.exe'))){
    $oldExe=Join-Path $InstallRoot 'Sokna.PrintAgent.Service.exe'
    try{& sc.exe config $service binPath= \"`\"$oldExe`\"\" start= delayed-auto obj= LocalSystem | Out-Null;Start-Service $service -ErrorAction SilentlyContinue}catch{}
  }"""
    new = """if($hadPrevious){
    $oldExe=Join-Path $InstallRoot 'Service\\Sokna.PrintAgent.Service.exe'
    if(-not (Test-Path $oldExe -PathType Leaf)){$oldExe=Join-Path $InstallRoot 'Sokna.PrintAgent.Service.exe'}
    if(Test-Path $oldExe -PathType Leaf){
      try{& sc.exe config $service binPath= \"`\"$oldExe`\"\" start= delayed-auto obj= LocalSystem | Out-Null;Start-Service $service -ErrorAction SilentlyContinue}catch{}
    }
  }"""
    if old in s:
        s = s.replace(old, new)

    assert "Service\\Sokna.PrintAgent.Service.exe" in s
    write(p, s)


def ensure_smoke_layout(root: Path) -> None:
    p = root / "scripts" / "Test-Windows-Install.ps1"
    s = read(p)
    pairs = [
        ("Join-Path $installRoot 'Sokna.PrintAgent.Service.exe'", "Join-Path $installRoot 'Service\\Sokna.PrintAgent.Service.exe'"),
        ("Join-Path $installRoot 'Sokna.PrintAgent.Worker.exe'", "Join-Path $installRoot 'Worker\\Sokna.PrintAgent.Worker.exe'"),
        ("Join-Path $installRoot 'Sokna.PrintAgent.Control.exe'", "Join-Path $installRoot 'Control\\Sokna.PrintAgent.Control.exe'"),
    ]
    for old, new in pairs:
        if old in s:
            s = s.replace(old, new)
    for token in ("Service\\Sokna.PrintAgent.Service.exe", "Worker\\Sokna.PrintAgent.Worker.exe", "Control\\Sokna.PrintAgent.Control.exe"):
        assert token in s
    write(p, s)


def validate(root: Path) -> None:
    build = read(root / "scripts" / "Build-Agent.ps1")
    service = read(root / "src" / "Sokna.PrintAgent.Service" / "PrintAgentService.cs")
    install = read(root / "installer" / "Install-SoknaPrintAgent.ps1")
    smoke = read(root / "scripts" / "Test-Windows-Install.ps1")
    props = read(root / "Directory.Packages.props")
    assert "$layout=[ordered]@{" in build and "collisionHashes" not in build
    assert '"..","Worker","Sokna.PrintAgent.Worker.exe"' in service
    assert "Service\\Sokna.PrintAgent.Service.exe" in install
    assert "Worker\\Sokna.PrintAgent.Worker.exe" in smoke
    assert 'SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.12"' in props


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("source", type=Path)
    args = ap.parse_args()
    root = args.source.resolve()
    ensure_sqlite_security(root)
    ensure_dotnet10_source_compat(root)
    ensure_isolated_package(root)
    ensure_worker_path(root)
    ensure_installer_layout(root)
    ensure_smoke_layout(root)
    validate(root)
    print("PATCH_AGENT_SOURCE_OK", root)


if __name__ == "__main__":
    main()
