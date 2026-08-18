param(
  [Parameter(Mandatory=$true)][string]$Artifacts,
  [string]$Version='6.0.0'
)
$ErrorActionPreference='Stop'
$setup=Join-Path $Artifacts "Sokna-Print-Agent-$Version-Setup.exe"
if(-not (Test-Path $setup -PathType Leaf)){throw "Setup.exe missing: $setup"}
$service='SoknaPrintAgent6'
$installRoot=Join-Path $env:ProgramFiles 'Sokna\PrintAgent'
$dataRoot=Join-Path $env:ProgramData 'Sokna\PrintAgent'
$setupLogRoot=Join-Path $env:ProgramData 'Sokna\PrintAgentSetup\logs'
$health=Join-Path $dataRoot 'health.json'

# Hosted Windows runners are disposable; make the smoke test deterministic.
if(Get-Service $service -ErrorAction SilentlyContinue){
  try{Stop-Service $service -Force -ErrorAction SilentlyContinue}catch{}
  & sc.exe delete $service | Out-Null
  Start-Sleep -Seconds 2
}
Remove-Item $installRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $setupLogRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== Install through the real embedded Setup.exe =='
$installStarted=Get-Date
$stdout=Join-Path $env:RUNNER_TEMP 'sokna-setup-smoke.stdout.log'
$stderr=Join-Path $env:RUNNER_TEMP 'sokna-setup-smoke.stderr.log'
Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue
$proc=Start-Process -FilePath $setup -ArgumentList '/quiet' -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if($proc.ExitCode -ne 0){
  if(Test-Path $stdout){Write-Host '=== Setup stdout ===';Get-Content $stdout -ErrorAction SilentlyContinue}
  if(Test-Path $stderr){Write-Host '=== Setup stderr ===';Get-Content $stderr -ErrorAction SilentlyContinue}
  if(Test-Path $setupLogRoot){Write-Host '=== Setup diagnostic JSON ===';Get-ChildItem $setupLogRoot -File | Sort-Object LastWriteTime | Select-Object -Last 3 | ForEach-Object {Get-Content $_.FullName -Raw}}
  throw "Setup.exe returned $($proc.ExitCode)"
}

$svc=Get-Service $service -ErrorAction Stop
if($svc.Status -ne 'Running'){throw "Service not running after install: $($svc.Status)"}
if(Get-Process 'Sokna.PrintAgent.Control' -ErrorAction SilentlyContinue){throw 'Control App unexpectedly required/launched for Service liveness.'}

if(-not (Test-Path $health -PathType Leaf)){throw 'Fresh health.json not produced.'}
if((Get-Item $health).LastWriteTime -lt $installStarted.AddSeconds(-2)){throw 'health.json is stale.'}
$h=Get-Content $health -Raw | ConvertFrom-Json
if(-not $h.updated_at){throw 'health.json missing updated_at.'}
if(-not $h.service_account_context){throw 'Service is not reporting service-account context.'}

foreach($required in @(
  'Service\Sokna.PrintAgent.Service.exe',
  'Worker\Sokna.PrintAgent.Worker.exe',
  'Control\Sokna.PrintAgent.Control.exe',
  'Uninstall-SoknaPrintAgent.ps1'
)){
  if(-not (Test-Path (Join-Path $installRoot $required) -PathType Leaf)){throw "Installed component missing: $required"}
}

$cfg=& sc.exe qc $service | Out-String
if($LASTEXITCODE -ne 0){throw "sc qc failed: $LASTEXITCODE"}
$expectedExe=(Join-Path $installRoot 'Service\Sokna.PrintAgent.Service.exe')
if($cfg -notmatch [Regex]::Escape($expectedExe)){throw 'Service binary path does not point to isolated Service directory.'}
$svcReg="HKLM:\SYSTEM\CurrentControlSet\Services\$service"
$svcProps=Get-ItemProperty $svcReg -ErrorAction Stop
if([int]$svcProps.Start -ne 2){throw 'Service start type is not Automatic.'}
if([int]$svcProps.DelayedAutoStart -ne 1){throw 'Service is not Automatic Delayed Start.'}

$failure=& sc.exe qfailure $service | Out-String
if($LASTEXITCODE -ne 0){throw "sc qfailure failed: $LASTEXITCODE"}
if($failure -notmatch 'RESTART'){throw 'Service Recovery restart action is missing.'}

# Create durable sentinels that must survive uninstall by default.
$sentinel=Join-Path $dataRoot 'ci-preserve-sentinel.txt'
Set-Content $sentinel 'preserve-me' -Encoding ascii
$dbSentinel=Join-Path $dataRoot 'queue.db.ci-preserve-sentinel'
Set-Content $dbSentinel 'preserve-db-location' -Encoding ascii

Write-Host '== Uninstall; ProgramData must be preserved by default =='
$uninstall=Join-Path $installRoot 'Uninstall-SoknaPrintAgent.ps1'
if(-not (Test-Path $uninstall -PathType Leaf)){throw 'Installed uninstaller missing.'}
& powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $uninstall
if($LASTEXITCODE -ne 0){throw "Uninstaller returned $LASTEXITCODE"}
if(Get-Service $service -ErrorAction SilentlyContinue){throw 'Service still exists after uninstall.'}
if(Test-Path $installRoot){throw 'Program Files installation remains after uninstall.'}
if(-not (Test-Path $sentinel -PathType Leaf)){throw 'ProgramData was deleted by default uninstall.'}
if(-not (Test-Path $dbSentinel -PathType Leaf)){throw 'Durable data location was deleted by default uninstall.'}

# CI cleanup only, after preservation has been proven.
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'PASS Windows Setup/Service/Uninstall smoke test' -ForegroundColor Green
