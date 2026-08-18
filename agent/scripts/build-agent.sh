#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-win-x64}"
VERSION="${VERSION:-6.0.0}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
OUTPUT="${OUTPUT:-$ROOT/artifacts}"
SOLUTION="$ROOT/Sokna.PrintAgent.slnx"
WINDOWS_TFM="net10.0-windows10.0.19041.0"

cd -- "$ROOT"

need() { command -v "$1" >/dev/null 2>&1 || { echo "ERROR: required tool not found: $1" >&2; exit 3; }; }
need dotnet
need python3

SDK="$(dotnet --version)"
case "$SDK" in
  10.*) ;;
  *) echo "ERROR: .NET 10 SDK required; found $SDK" >&2; exit 4;;
esac

rm -rf -- "$OUTPUT"
mkdir -p -- "$OUTPUT"

echo "== Restore =="
dotnet restore "$SOLUTION" --locked-mode || dotnet restore "$SOLUTION"

echo "== Build =="
dotnet build "$SOLUTION" -c "$CONFIGURATION" --no-restore

echo "== Cross-platform agent tests =="
dotnet run --project "$ROOT/tests/Sokna.PrintAgent.Tests/Sokna.PrintAgent.Tests.csproj" -c "$CONFIGURATION" --no-build

projects=(Sokna.PrintAgent.Service Sokna.PrintAgent.Worker Sokna.PrintAgent.Control)
for project in "${projects[@]}"; do
  proj="$ROOT/src/$project/$project.csproj"
  echo "== Runtime restore $project ($RUNTIME) =="
  dotnet restore "$proj" -r "$RUNTIME"
  echo "== Publish $project ($RUNTIME) =="
  dotnet publish "$proj" \
    -c "$CONFIGURATION" -r "$RUNTIME" --self-contained true --no-restore \
    -p:PublishSingleFile=false -o "$OUTPUT/$project"
done

PACKAGE="$OUTPUT/package"
PAYLOAD="$PACKAGE/payload"
DOCS="$PACKAGE/docs"
mkdir -p -- "$PAYLOAD" "$DOCS"

# The installed uninstaller is part of the verified payload and therefore covered by PAYLOAD_MANIFEST.json.
cp "$ROOT/installer/Uninstall-SoknaPrintAgent.ps1" "$PAYLOAD/Uninstall-SoknaPrintAgent.ps1"

# Merge published projects into one install directory, but reject non-identical name collisions.
python3 - "$OUTPUT" "$PAYLOAD" <<'PY'
from pathlib import Path
import hashlib, shutil, sys
out=Path(sys.argv[1]); payload=Path(sys.argv[2])
projects=['Sokna.PrintAgent.Service','Sokna.PrintAgent.Worker','Sokna.PrintAgent.Control']
seen={}
for project in projects:
    src=out/project
    if not src.is_dir(): raise SystemExit(f'Missing publish directory: {src}')
    for f in sorted(p for p in src.iterdir() if p.is_file()):
        digest=hashlib.sha256(f.read_bytes()).hexdigest()
        if f.name in seen and seen[f.name] != digest:
            raise SystemExit(f'Published file collision with different content: {f.name}')
        seen[f.name]=digest
        shutil.copy2(f, payload/f.name)
for required in ['Sokna.PrintAgent.Service.exe','Sokna.PrintAgent.Worker.exe','Sokna.PrintAgent.Control.exe']:
    if not (payload/required).is_file(): raise SystemExit(f'Missing required Windows executable: {required}')
print(f'Merged payload files: {len(seen)}')
PY

cp "$ROOT/installer/Install-SoknaPrintAgent.ps1" "$PACKAGE/"
cp "$ROOT/installer/Uninstall-SoknaPrintAgent.ps1" "$PACKAGE/"
cp "$ROOT/README_FA.md" "$PACKAGE/"
cp "$ROOT"/docs/*.md "$DOCS/"
printf '%s\n' "$VERSION" > "$PACKAGE/VERSION.txt"

python3 - "$PACKAGE" "$SDK" "$VERSION" "$RUNTIME" "$CONFIGURATION" "$WINDOWS_TFM" <<'PY'
from pathlib import Path
import hashlib, json, sys, datetime
package=Path(sys.argv[1]); sdk,version,runtime,config,tfm=sys.argv[2:]
payload=package/'payload'
manifest=[]
for f in sorted(p for p in payload.rglob('*') if p.is_file()):
    raw=f.read_bytes()
    manifest.append({'path':f.relative_to(package).as_posix(),'size':len(raw),'sha256':hashlib.sha256(raw).hexdigest()})
(package/'PAYLOAD_MANIFEST.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
info={
 'agent_version':version,'protocol_version':4,'runtime':runtime,'configuration':config,
 'dotnet_sdk':sdk,'built_at_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'target_framework':tfm,'self_contained':True,
 'physical_printer_validation':'PENDING — PRODUCTION GATE'
}
(package/'BUILD_INFO.json').write_text(json.dumps(info,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
PY

PACKAGE_ZIP="$OUTPUT/Sokna-Print-Agent-$VERSION-$RUNTIME.zip"
python3 - "$PACKAGE" "$PACKAGE_ZIP" <<'PY'
from pathlib import Path
import sys, zipfile
root=Path(sys.argv[1]); dest=Path(sys.argv[2])
with zipfile.ZipFile(dest,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for f in sorted(p for p in root.rglob('*') if p.is_file()):
        z.write(f,f.relative_to(root).as_posix())
PY

# Publish a real single-file Windows bootstrapper with the verified package embedded as a resource.
SETUP_OUT="$OUTPUT/Setup"
mkdir -p -- "$SETUP_OUT"
echo "== Runtime restore Setup ($RUNTIME) =="
dotnet restore "$ROOT/src/Sokna.PrintAgent.Setup/Sokna.PrintAgent.Setup.csproj" -r "$RUNTIME"
echo "== Publish embedded Setup.exe =="
dotnet publish "$ROOT/src/Sokna.PrintAgent.Setup/Sokna.PrintAgent.Setup.csproj" \
  -c "$CONFIGURATION" -r "$RUNTIME" --self-contained true --no-restore \
  -p:PayloadZip="$PACKAGE_ZIP" -p:PublishSingleFile=true -o "$SETUP_OUT"

SETUP_EXE="$SETUP_OUT/Sokna-Print-Agent-$VERSION-Setup.exe"
if [[ ! -f "$SETUP_EXE" ]]; then
  echo "ERROR: Setup executable not produced: $SETUP_EXE" >&2
  exit 5
fi
cp "$SETUP_EXE" "$OUTPUT/"
SETUP_EXE="$OUTPUT/$(basename "$SETUP_EXE")"

python3 - "$OUTPUT" "$PACKAGE_ZIP" "$SETUP_EXE" "$VERSION" <<'PY'
from pathlib import Path
import hashlib, json, sys
out=Path(sys.argv[1]); package=Path(sys.argv[2]); setup=Path(sys.argv[3]); version=sys.argv[4]
rows=[]
for f in (package,setup):
    raw=f.read_bytes(); rows.append((hashlib.sha256(raw).hexdigest(),f.name,len(raw)))
(out/f'SHA256SUMS-Agent-{version}.txt').write_text(''.join(f'{h}  {name}\n' for h,name,_ in rows),encoding='ascii')
(out/f'BUILD_ARTIFACTS-Agent-{version}.json').write_text(json.dumps([
 {'file':name,'size':size,'sha256':h} for h,name,size in rows
],indent=2)+'\n',encoding='utf-8')
for h,name,size in rows: print(f'BUILT {name} ({size} bytes) SHA256={h}')
PY

echo "SUCCESS: Agent package and Windows Setup.exe built in $OUTPUT"
