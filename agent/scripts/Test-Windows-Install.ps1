param(
  [Parameter(Mandatory=$true)][string]$Artifacts,
  [string]$Version=''
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if([string]::IsNullOrWhiteSpace($Version)){
  [xml]$buildProps=Get-Content (Join-Path $root 'Directory.Build.props') -Raw
  $Version=[string]$buildProps.Project.PropertyGroup.SoknaAgentVersion
}
if([string]::IsNullOrWhiteSpace($Version)){throw 'Agent version could not be resolved.'}
$setup=Join-Path $Artifacts "Sokna-Print-Agent-$Version-Setup.exe"
if(-not (Test-Path $setup -PathType Leaf)){throw "Setup.exe missing: $setup"}
$service='SoknaPrintAgent6'
$installRoot=Join-Path $env:ProgramFiles 'Sokna\PrintAgent'
$dataRoot=Join-Path $env:ProgramData 'Sokna\PrintAgent'
$setupLogRoot=Join-Path $env:ProgramData 'Sokna\PrintAgentSetup\logs'
$health=Join-Path $dataRoot 'health.json'
$evidenceDir=Join-Path $Artifacts 'windows-smoke-evidence'
$gateEvidence=Join-Path $evidenceDir 'INSTALL_GATE_EVIDENCE.txt'
$startShortcut=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)) 'Sokna Print Agent.lnk'
$desktopShortcut=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)) 'Sokna Print Agent.lnk'
$regPath='HKLM:\SOFTWARE\Sokna\PrintAgent'
New-Item $evidenceDir -ItemType Directory -Force | Out-Null
"version=$Version`ntimestamp_start=$((Get-Date).ToUniversalTime().ToString('o'))" | Set-Content $gateEvidence

function Invoke-SetupQuiet([string]$Stdout,[string]$Stderr){
  Remove-Item $Stdout,$Stderr -Force -ErrorAction SilentlyContinue
  return Start-Process -FilePath $setup -ArgumentList '/quiet' -Wait -PassThru -RedirectStandardOutput $Stdout -RedirectStandardError $Stderr
}

function New-FailureInjectedPackage(){
  $package=Join-Path $Artifacts 'package'
  if(-not (Test-Path $package -PathType Container)){throw "Build package directory missing: $package"}
  $copy=Join-Path $env:RUNNER_TEMP ("sokna-rollback-gate-"+[Guid]::NewGuid().ToString('N'))
  Copy-Item $package $copy -Recurse -Force
  $script=Join-Path $copy 'Install-SoknaPrintAgent.ps1'
  if(-not (Test-Path $script -PathType Leaf)){throw 'Failure-injection installer script missing.'}
  $text=Get-Content $script -Raw
  $needle="  Set-InstallStage 'service_recovery'"
  if(-not $text.Contains($needle)){throw 'Failure-injection anchor missing from installer.'}
  $replacement="  throw 'CI_INJECTED_FAILURE_AFTER_SERVICE_REGISTRATION'`r`n$needle"
  $text=$text.Replace($needle,$replacement)
  [IO.File]::WriteAllText($script,$text,(New-Object Text.UTF8Encoding($false)))
  return [pscustomobject]@{Root=$copy;Script=$script}
}

function Invoke-InjectedFailure([string]$Scenario){
  $pkg=New-FailureInjectedPackage
  $stdout=Join-Path $env:RUNNER_TEMP "sokna-$Scenario-rollback.stdout.log"
  $stderr=Join-Path $env:RUNNER_TEMP "sokna-$Scenario-rollback.stderr.log"
  try{
    Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue
    $proc=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$pkg.Script) -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $out=if(Test-Path $stdout){Get-Content $stdout -Raw}else{''}
    $err=if(Test-Path $stderr){Get-Content $stderr -Raw}else{''}
    "rollback_${Scenario}_exit=$($proc.ExitCode)" | Out-File $gateEvidence -Append
    "rollback_${Scenario}_verified_marker=$($out -match 'SOKNA_ROLLBACK_RESULT=success')" | Out-File $gateEvidence -Append
    if($proc.ExitCode -eq 0){throw "Injected $Scenario failure unexpectedly succeeded."}
    if($out -notmatch 'SOKNA_ROLLBACK_RESULT=success'){
      Write-Host "=== $Scenario rollback stdout ===";Write-Host $out
      Write-Host "=== $Scenario rollback stderr ===";Write-Host $err
      throw "Injected $Scenario failure did not emit verified rollback success marker."
    }
    if($err -match 'SOKNA_RECOVERY_FAILURE'){throw "Injected $Scenario failure reported recovery failure."}
    return [pscustomobject]@{Stdout=$out;Stderr=$err;ExitCode=$proc.ExitCode}
  }
  finally{
    Remove-Item $pkg.Root -Recurse -Force -ErrorAction SilentlyContinue
  }
}

if(Get-Service $service -ErrorAction SilentlyContinue){
  try{Stop-Service $service -Force -ErrorAction SilentlyContinue}catch{}
  & sc.exe delete $service | Out-Null
  Start-Sleep -Seconds 2
}
Remove-Item $installRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $setupLogRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $startShortcut,$desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== Install through the real embedded Setup.exe =='
$installStarted=Get-Date
$stdout=Join-Path $env:RUNNER_TEMP 'sokna-setup-smoke.stdout.log'
$stderr=Join-Path $env:RUNNER_TEMP 'sokna-setup-smoke.stderr.log'
$proc=Invoke-SetupQuiet $stdout $stderr
"setup_exit=$($proc.ExitCode)" | Out-File $gateEvidence -Append
if($proc.ExitCode -ne 0){
  if(Test-Path $stdout){Write-Host '=== Setup stdout ===';Get-Content $stdout -ErrorAction SilentlyContinue}
  if(Test-Path $stderr){Write-Host '=== Setup stderr ===';Get-Content $stderr -ErrorAction SilentlyContinue}
  if(Test-Path $setupLogRoot){Write-Host '=== Setup diagnostic JSON ===';Get-ChildItem $setupLogRoot -File | Sort-Object LastWriteTime | Select-Object -Last 3 | ForEach-Object {Get-Content $_.FullName -Raw}}
  throw "Setup.exe returned $($proc.ExitCode)"
}

$svc=Get-Service $service -ErrorAction Stop
if($svc.Status -ne 'Running'){throw "Service not running after install: $($svc.Status)"}
if(Get-Process 'Sokna.PrintAgent.Control' -ErrorAction SilentlyContinue){throw 'Control App unexpectedly required/launched for Service liveness.'}
"service_status=$($svc.Status)" | Out-File $gateEvidence -Append
"control_process_running=False" | Out-File $gateEvidence -Append

if(-not (Test-Path $health -PathType Leaf)){throw 'Fresh health.json not produced.'}
if((Get-Item $health).LastWriteTime -lt $installStarted.AddSeconds(-2)){throw 'health.json is stale.'}
$h=Get-Content $health -Raw | ConvertFrom-Json
if(-not $h.updated_at){throw 'health.json missing updated_at.'}
if(-not $h.service_account_context){throw 'Service is not reporting service-account context.'}
"health_updated_at=$($h.updated_at)" | Out-File $gateEvidence -Append
"service_account_context=$($h.service_account_context)" | Out-File $gateEvidence -Append
"health_state=$($h.state)" | Out-File $gateEvidence -Append
"`n=== HEALTH.JSON BEFORE UNINSTALL ===" | Out-File $gateEvidence -Append
Get-Content $health -Raw | Out-File $gateEvidence -Append

foreach($required in @(
  'Service\Sokna.PrintAgent.Service.exe',
  'Worker\Sokna.PrintAgent.Worker.exe',
  'Control\Sokna.PrintAgent.Control.exe',
  'Uninstall-SoknaPrintAgent.ps1'
)){
  if(-not (Test-Path (Join-Path $installRoot $required) -PathType Leaf)){throw "Installed component missing: $required"}
  "installed_component=$required" | Out-File $gateEvidence -Append
}
if(-not (Test-Path $startShortcut -PathType Leaf)){throw 'Start Menu shortcut was not created.'}
if(-not (Test-Path $desktopShortcut -PathType Leaf)){throw 'Desktop shortcut was not created.'}
"start_menu_shortcut=True" | Out-File $gateEvidence -Append
"desktop_shortcut=True" | Out-File $gateEvidence -Append

$agentReg=Get-ItemProperty $regPath -ErrorAction Stop
if([string]$agentReg.Version -ne $Version){throw "Registry version mismatch: $($agentReg.Version) != $Version"}
"registry_agent_version=$($agentReg.Version)" | Out-File $gateEvidence -Append

$cfg=& sc.exe qc $service | Out-String
if($LASTEXITCODE -ne 0){throw "sc qc failed: $LASTEXITCODE"}
$expectedExe=(Join-Path $installRoot 'Service\Sokna.PrintAgent.Service.exe')
if($cfg -notmatch [Regex]::Escape($expectedExe)){throw 'Service binary path does not point to isolated Service directory.'}
"`n=== SC QC BEFORE UNINSTALL ===" | Out-File $gateEvidence -Append
$cfg | Out-File $gateEvidence -Append
$svcReg="HKLM:\SYSTEM\CurrentControlSet\Services\$service"
$svcProps=Get-ItemProperty $svcReg -ErrorAction Stop
if([int]$svcProps.Start -ne 2){throw 'Service start type is not Automatic.'}
if([int]$svcProps.DelayedAutoStart -ne 1){throw 'Service is not Automatic Delayed Start.'}
"registry_start=$([int]$svcProps.Start)" | Out-File $gateEvidence -Append
"registry_delayed_auto_start=$([int]$svcProps.DelayedAutoStart)" | Out-File $gateEvidence -Append

$failure=& sc.exe qfailure $service | Out-String
if($LASTEXITCODE -ne 0){throw "sc qfailure failed: $LASTEXITCODE"}
if($failure -notmatch 'RESTART'){throw 'Service Recovery restart action is missing.'}
"`n=== SC QFAILURE BEFORE UNINSTALL ===" | Out-File $gateEvidence -Append
$failure | Out-File $gateEvidence -Append

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
if(Test-Path $startShortcut -PathType Leaf){throw 'Start Menu shortcut remains after uninstall.'}
if(Test-Path $desktopShortcut -PathType Leaf){throw 'Desktop shortcut remains after uninstall.'}
if(-not (Test-Path $sentinel -PathType Leaf)){throw 'ProgramData was deleted by default uninstall.'}
if(-not (Test-Path $dbSentinel -PathType Leaf)){throw 'Durable data location was deleted by default uninstall.'}
"uninstall_service_removed=True" | Out-File $gateEvidence -Append
"uninstall_program_files_removed=True" | Out-File $gateEvidence -Append
"uninstall_shortcuts_removed=True" | Out-File $gateEvidence -Append
"uninstall_programdata_preserved=True" | Out-File $gateEvidence -Append

Write-Host '== Synthetic fresh-install failure rollback gate =='
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
Invoke-InjectedFailure 'fresh' | Out-Null
if(Get-Service $service -ErrorAction SilentlyContinue){throw 'Fresh-install rollback left an orphan Windows Service.'}
if(Test-Path $installRoot){throw 'Fresh-install rollback left Program Files behind.'}
if(Test-Path $regPath){throw 'Fresh-install rollback left installation registry state behind.'}
"rollback_fresh_service_orphan=False" | Out-File $gateEvidence -Append
"rollback_fresh_program_files_removed=True" | Out-File $gateEvidence -Append
"rollback_fresh_registry_removed=True" | Out-File $gateEvidence -Append

Write-Host '== Synthetic upgrade failure rollback gate =='
$upgradeSetupStdout=Join-Path $env:RUNNER_TEMP 'sokna-upgrade-baseline.stdout.log'
$upgradeSetupStderr=Join-Path $env:RUNNER_TEMP 'sokna-upgrade-baseline.stderr.log'
$upgradeProc=Invoke-SetupQuiet $upgradeSetupStdout $upgradeSetupStderr
if($upgradeProc.ExitCode -ne 0){throw "Upgrade rollback baseline install failed: $($upgradeProc.ExitCode)"}
$beforeService=Get-Service $service -ErrorAction Stop
if($beforeService.Status -ne 'Running'){throw 'Upgrade rollback baseline Service is not running.'}
$beforeExe=Join-Path $installRoot 'Service\Sokna.PrintAgent.Service.exe'
$beforeHash=(Get-FileHash $beforeExe -Algorithm SHA256).Hash
$beforeVersion=[string](Get-ItemProperty $regPath -ErrorAction Stop).Version
$upgradeSentinel=Join-Path $dataRoot 'ci-upgrade-rollback-sentinel.txt'
Set-Content $upgradeSentinel 'preserve-through-rollback' -Encoding ascii
Invoke-InjectedFailure 'upgrade' | Out-Null
$afterService=Get-Service $service -ErrorAction Stop
if($afterService.Status -ne 'Running'){throw "Upgrade rollback did not restore running Service: $($afterService.Status)"}
if(-not (Test-Path $beforeExe -PathType Leaf)){throw 'Upgrade rollback did not restore previous Service binary.'}
$afterHash=(Get-FileHash $beforeExe -Algorithm SHA256).Hash
if($afterHash -ne $beforeHash){throw 'Upgrade rollback previous Service binary hash changed.'}
$afterVersion=[string](Get-ItemProperty $regPath -ErrorAction Stop).Version
if($afterVersion -ne $beforeVersion){throw "Upgrade rollback registry version changed: $afterVersion != $beforeVersion"}
if(-not (Test-Path $upgradeSentinel -PathType Leaf)){throw 'Upgrade rollback lost ProgramData sentinel.'}
"rollback_upgrade_service_running=True" | Out-File $gateEvidence -Append
"rollback_upgrade_binary_restored=True" | Out-File $gateEvidence -Append
"rollback_upgrade_version_restored=True" | Out-File $gateEvidence -Append
"rollback_upgrade_programdata_preserved=True" | Out-File $gateEvidence -Append

$uninstall=Join-Path $installRoot 'Uninstall-SoknaPrintAgent.ps1'
& powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $uninstall
if($LASTEXITCODE -ne 0){throw "Final rollback-gate uninstaller returned $LASTEXITCODE"}
if(Get-Service $service -ErrorAction SilentlyContinue){throw 'Service still exists after final rollback-gate uninstall.'}

"timestamp_end=$((Get-Date).ToUniversalTime().ToString('o'))" | Out-File $gateEvidence -Append
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $setupLogRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'PASS Windows Setup/Service/Uninstall/Rollback smoke test' -ForegroundColor Green
