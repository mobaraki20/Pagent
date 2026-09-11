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
$uninstallRegPath='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SoknaPrintAgent6'
New-Item $evidenceDir -ItemType Directory -Force | Out-Null
"version=$Version`ntimestamp_start=$((Get-Date).ToUniversalTime().ToString('o'))" | Set-Content $gateEvidence

function Invoke-SetupQuiet([string]$Stdout,[string]$Stderr,[string[]]$ExtraArgs=@()){
  Remove-Item $Stdout,$Stderr -Force -ErrorAction SilentlyContinue
  $arguments=@('/quiet')+$ExtraArgs
  return Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $Stdout -RedirectStandardError $Stderr
}

function Test-UsersReadExecuteAcl([string]$Path){
  $usersSid='S-1-5-32-545'
  $needed=[int][Security.AccessControl.FileSystemRights]::ReadAndExecute
  foreach($rule in (Get-Acl $Path -ErrorAction Stop).Access){
    try{$sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value}catch{continue}
    $rights=[int]$rule.FileSystemRights
    if($sid -eq $usersSid -and $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and (($rights -band $needed) -eq $needed)){return $true}
  }
  return $false
}

function Assert-Shortcut([string]$Path,[bool]$Expected,[string]$ControlExe,[string]$Label){
  $exists=Test-Path $Path -PathType Leaf
  if($exists -ne $Expected){throw "$Label shortcut expected=$Expected actual=$exists"}
  if(-not $Expected){return}
  $shortcut=$null
  $shell=New-Object -ComObject WScript.Shell
  try{
    $shortcut=$shell.CreateShortcut($Path)
    if(-not [string]::Equals([IO.Path]::GetFullPath($shortcut.TargetPath),[IO.Path]::GetFullPath($ControlExe),[StringComparison]::OrdinalIgnoreCase)){throw "$Label shortcut target mismatch."}
    if(-not ([string]$shortcut.IconLocation).StartsWith($ControlExe,[StringComparison]::OrdinalIgnoreCase)){throw "$Label shortcut icon mismatch."}
  }
  finally{
    if($null -ne $shortcut){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null}
    if($null -ne $shell){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null}
  }
}

function Assert-InstalledAppsRegistration(){
  if(-not (Test-Path $uninstallRegPath)){throw 'Windows Installed Apps registry entry is missing.'}
  $arp=Get-ItemProperty $uninstallRegPath -ErrorAction Stop
  if([string]$arp.DisplayName -ne 'Sokna Print Agent'){throw "Installed Apps DisplayName mismatch: $($arp.DisplayName)"}
  if([string]$arp.DisplayVersion -ne $Version){throw "Installed Apps DisplayVersion mismatch: $($arp.DisplayVersion)"}
  if([string]$arp.Publisher -ne 'Sokna Group'){throw "Installed Apps Publisher mismatch: $($arp.Publisher)"}
  if(-not [string]::Equals([IO.Path]::GetFullPath([string]$arp.InstallLocation),[IO.Path]::GetFullPath($installRoot),[StringComparison]::OrdinalIgnoreCase)){throw 'Installed Apps InstallLocation mismatch.'}
  if([string]::IsNullOrWhiteSpace([string]$arp.UninstallString) -or ([string]$arp.UninstallString -notmatch 'Uninstall-SoknaPrintAgent\.ps1')){throw 'Installed Apps UninstallString is invalid.'}
  if([string]::IsNullOrWhiteSpace([string]$arp.QuietUninstallString) -or ([string]$arp.QuietUninstallString -notmatch '-Quiet')){throw 'Installed Apps QuietUninstallString is invalid.'}
  if([int]$arp.NoModify -ne 1 -or [int]$arp.NoRepair -ne 1){throw 'Installed Apps NoModify/NoRepair policy mismatch.'}
  if([int]$arp.EstimatedSize -le 0){throw 'Installed Apps EstimatedSize must be positive.'}
  if(([string]$arp.DisplayIcon) -notmatch 'Sokna\.PrintAgent\.Control\.exe'){throw 'Installed Apps DisplayIcon is invalid.'}
  return $arp
}

function Invoke-RegisteredQuietUninstall(){
  $arp=Assert-InstalledAppsRegistration
  $command=[string]$arp.QuietUninstallString
  if($command -notmatch '^"([^"]+)"\s+(.+)$'){throw "QuietUninstallString cannot be parsed: $command"}
  $fileName=$Matches[1]
  $arguments=$Matches[2]
  return Start-Process -FilePath $fileName -ArgumentList $arguments -Wait -PassThru
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
Remove-Item $regPath,$uninstallRegPath -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== Verify official .NET Runtime download and Microsoft signature =='
$runtimeVerify=Start-Process -FilePath $setup -ArgumentList @('/verify-runtime-download') -Wait -PassThru
"runtime_download_signature_check_exit=$($runtimeVerify.ExitCode)" | Out-File $gateEvidence -Append
if($runtimeVerify.ExitCode -ne 0){throw "Setup failed Microsoft .NET Runtime download/signature verification: $($runtimeVerify.ExitCode)"}
"runtime_download_signature_verified=True" | Out-File $gateEvidence -Append

Write-Host '== Fresh install through real embedded Setup.exe =='
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
foreach($component in @('Service','Worker','Control')){
  $dir=Join-Path $installRoot $component
  $runtimeConfig=Get-ChildItem $dir -Filter '*.runtimeconfig.json' -File | Select-Object -First 1
  if($null -eq $runtimeConfig){throw "Framework-dependent runtimeconfig missing after install: $component"}
  $runtimeText=Get-Content $runtimeConfig.FullName -Raw
  if($runtimeText -notmatch 'net10\.0' -or $runtimeText -notmatch '10\.0\.0'){throw "Runtimeconfig does not require .NET 10 as expected: $component"}
  if(Test-Path (Join-Path $dir 'coreclr.dll') -PathType Leaf){throw "Component unexpectedly ships private coreclr.dll: $component"}
  "framework_dependent_component=$component" | Out-File $gateEvidence -Append
}
"framework_dependent_payload_verified=True" | Out-File $gateEvidence -Append

if(-not (Test-UsersReadExecuteAcl $installRoot)){throw 'Built-in Users does not have ReadAndExecute on Program Files installation.'}
"program_files_users_read_execute=True" | Out-File $gateEvidence -Append
$controlExe=Join-Path $installRoot 'Control\Sokna.PrintAgent.Control.exe'
Assert-Shortcut $startShortcut $true $controlExe 'Start Menu'
Assert-Shortcut $desktopShortcut $false $controlExe 'Desktop'
"fresh_start_menu_shortcut=True" | Out-File $gateEvidence -Append
"fresh_desktop_shortcut=False" | Out-File $gateEvidence -Append
$agentReg=Get-ItemProperty $regPath -ErrorAction Stop
if([int]$agentReg.CreateStartMenuShortcut -ne 1 -or [int]$agentReg.CreateDesktopShortcut -ne 0){throw 'Fresh shortcut preferences were not persisted.'}
$arp=Assert-InstalledAppsRegistration
"installed_apps_registered=True" | Out-File $gateEvidence -Append
"installed_apps_display_version=$($arp.DisplayVersion)" | Out-File $gateEvidence -Append
"installed_apps_estimated_size_kb=$($arp.EstimatedSize)" | Out-File $gateEvidence -Append

$extractedIcon=[System.Drawing.Icon]::ExtractAssociatedIcon($controlExe)
try{
  if($null -eq $extractedIcon -or $extractedIcon.Width -lt 16 -or $extractedIcon.Height -lt 16){throw 'Control executable does not contain a valid application icon.'}
  "control_icon=$($extractedIcon.Width)x$($extractedIcon.Height)" | Out-File $gateEvidence -Append
}
finally{if($null -ne $extractedIcon){$extractedIcon.Dispose()}}

$controlProcess=Start-Process -FilePath $controlExe -PassThru
try{
  $windowDeadline=(Get-Date).AddSeconds(20)
  do{
    Start-Sleep -Milliseconds 250
    $controlProcess.Refresh()
  }while(-not $controlProcess.HasExited -and $controlProcess.MainWindowHandle -eq 0 -and (Get-Date) -lt $windowDeadline)
  if($controlProcess.HasExited){throw 'Control app exited before the tray lifecycle test.'}
  if($controlProcess.MainWindowHandle -eq 0){throw 'Control app did not create its main window for the tray lifecycle test.'}
  if(-not $controlProcess.CloseMainWindow()){throw 'WM_CLOSE could not be sent to the Control app.'}
  Start-Sleep -Seconds 2
  $controlProcess.Refresh()
  if($controlProcess.HasExited){throw 'Control app exited instead of remaining active in System Tray.'}
  "close_to_tray=True" | Out-File $gateEvidence -Append
}
finally{
  if(-not $controlProcess.HasExited){Stop-Process -Id $controlProcess.Id -Force -ErrorAction SilentlyContinue}
  $controlProcess.Dispose()
}

Write-Host '== Same-version repair with intentionally restricted Program Files ACL =='
& icacls.exe $installRoot /inheritance:r /remove:g '*S-1-5-32-545' | Out-Null
if($LASTEXITCODE -ne 0){throw "Unable to remove Users ACL for regression injection: $LASTEXITCODE"}
& icacls.exe $installRoot /grant:r '*S-1-5-18:(OI)(CI)RX' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw "Unable to inject restricted ACL: $LASTEXITCODE"}
if(Test-UsersReadExecuteAcl $installRoot){throw 'Restricted ACL injection did not remove Users ReadAndExecute.'}
$repairStdout=Join-Path $env:RUNNER_TEMP 'sokna-same-version-repair.stdout.log'
$repairStderr=Join-Path $env:RUNNER_TEMP 'sokna-same-version-repair.stderr.log'
$repairProc=Invoke-SetupQuiet $repairStdout $repairStderr
"same_version_repair_exit=$($repairProc.ExitCode)" | Out-File $gateEvidence -Append
if($repairProc.ExitCode -ne 0){
  if(Test-Path $repairStdout){Write-Host '=== Repair stdout ===';Get-Content $repairStdout -ErrorAction SilentlyContinue}
  if(Test-Path $repairStderr){Write-Host '=== Repair stderr ===';Get-Content $repairStderr -ErrorAction SilentlyContinue}
  throw "Same-version repair failed: $($repairProc.ExitCode)"
}
$repairService=Get-Service $service -ErrorAction Stop
if($repairService.Status -ne 'Running'){throw "Service not running after same-version repair: $($repairService.Status)"}
if(-not (Test-UsersReadExecuteAcl $installRoot)){throw 'Same-version repair did not restore Users ReadAndExecute ACL.'}
Assert-Shortcut $startShortcut $true $controlExe 'Start Menu after repair'
Assert-Shortcut $desktopShortcut $false $controlExe 'Desktop after repair'
$agentReg=Get-ItemProperty $regPath -ErrorAction Stop
if([int]$agentReg.CreateStartMenuShortcut -ne 1 -or [int]$agentReg.CreateDesktopShortcut -ne 0){throw 'Same-version repair did not preserve shortcut preferences.'}
Assert-InstalledAppsRegistration|Out-Null
"same_version_repair_service_running=True" | Out-File $gateEvidence -Append
"same_version_repair_acl_restored=True" | Out-File $gateEvidence -Append
"same_version_repair_shortcuts_preserved=True" | Out-File $gateEvidence -Append

Write-Host '== Explicit shortcut preference change: Desktop-only =='
$toggleStdout=Join-Path $env:RUNNER_TEMP 'sokna-shortcut-toggle.stdout.log'
$toggleStderr=Join-Path $env:RUNNER_TEMP 'sokna-shortcut-toggle.stderr.log'
$toggleProc=Invoke-SetupQuiet $toggleStdout $toggleStderr @('/no-start-menu-shortcut','/desktop-shortcut')
if($toggleProc.ExitCode -ne 0){throw "Shortcut preference repair failed: $($toggleProc.ExitCode)"}
Assert-Shortcut $startShortcut $false $controlExe 'Start Menu after explicit disable'
Assert-Shortcut $desktopShortcut $true $controlExe 'Desktop after explicit enable'
$agentReg=Get-ItemProperty $regPath -ErrorAction Stop
if([int]$agentReg.CreateStartMenuShortcut -ne 0 -or [int]$agentReg.CreateDesktopShortcut -ne 1){throw 'Explicit shortcut preferences were not persisted.'}
"shortcut_toggle_start_menu=False" | Out-File $gateEvidence -Append
"shortcut_toggle_desktop=True" | Out-File $gateEvidence -Append

Write-Host '== Repair without flags must retain stored Desktop-only preference =='
$retainStdout=Join-Path $env:RUNNER_TEMP 'sokna-shortcut-retain.stdout.log'
$retainStderr=Join-Path $env:RUNNER_TEMP 'sokna-shortcut-retain.stderr.log'
$retainProc=Invoke-SetupQuiet $retainStdout $retainStderr
if($retainProc.ExitCode -ne 0){throw "Shortcut retention repair failed: $($retainProc.ExitCode)"}
Assert-Shortcut $startShortcut $false $controlExe 'Start Menu after retained repair'
Assert-Shortcut $desktopShortcut $true $controlExe 'Desktop after retained repair'
$agentReg=Get-ItemProperty $regPath -ErrorAction Stop
if([int]$agentReg.CreateStartMenuShortcut -ne 0 -or [int]$agentReg.CreateDesktopShortcut -ne 1){throw 'Stored shortcut preferences were not retained.'}
$arp=Assert-InstalledAppsRegistration
"shortcut_preferences_retained=True" | Out-File $gateEvidence -Append
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

Write-Host '== Uninstall through Windows Installed Apps command; ProgramData must be preserved =='
$uninstallProc=Invoke-RegisteredQuietUninstall
"registered_uninstall_exit=$($uninstallProc.ExitCode)" | Out-File $gateEvidence -Append
if($uninstallProc.ExitCode -ne 0){throw "Registered uninstaller returned $($uninstallProc.ExitCode)"}
if(Get-Service $service -ErrorAction SilentlyContinue){throw 'Service still exists after registered uninstall.'}
if(Test-Path $installRoot){throw 'Program Files installation remains after registered uninstall.'}
if(Test-Path $startShortcut -PathType Leaf){throw 'Start Menu shortcut remains after registered uninstall.'}
if(Test-Path $desktopShortcut -PathType Leaf){throw 'Desktop shortcut remains after registered uninstall.'}
if(Test-Path $uninstallRegPath){throw 'Windows Installed Apps registry entry remains after uninstall.'}
if(-not (Test-Path $sentinel -PathType Leaf)){throw 'ProgramData was deleted by default uninstall.'}
if(-not (Test-Path $dbSentinel -PathType Leaf)){throw 'Durable data location was deleted by default uninstall.'}
"uninstall_service_removed=True" | Out-File $gateEvidence -Append
"uninstall_program_files_removed=True" | Out-File $gateEvidence -Append
"uninstall_shortcuts_removed=True" | Out-File $gateEvidence -Append
"uninstall_installed_apps_entry_removed=True" | Out-File $gateEvidence -Append
"uninstall_programdata_preserved=True" | Out-File $gateEvidence -Append

Write-Host '== Synthetic fresh-install failure rollback gate =='
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $regPath,$uninstallRegPath -Recurse -Force -ErrorAction SilentlyContinue
Invoke-InjectedFailure 'fresh' | Out-Null
if(Get-Service $service -ErrorAction SilentlyContinue){throw 'Fresh-install rollback left an orphan Windows Service.'}
if(Test-Path $installRoot){throw 'Fresh-install rollback left Program Files behind.'}
if(Test-Path $regPath){throw 'Fresh-install rollback left installation registry state behind.'}
if(Test-Path $uninstallRegPath){throw 'Fresh-install rollback left Installed Apps registration behind.'}
"rollback_fresh_service_orphan=False" | Out-File $gateEvidence -Append
"rollback_fresh_program_files_removed=True" | Out-File $gateEvidence -Append
"rollback_fresh_registry_removed=True" | Out-File $gateEvidence -Append
"rollback_fresh_installed_apps_removed=True" | Out-File $gateEvidence -Append

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
$beforeArp=Assert-InstalledAppsRegistration
$beforeArpVersion=[string]$beforeArp.DisplayVersion
$beforeUninstallString=[string]$beforeArp.UninstallString
$beforeStartExists=Test-Path $startShortcut -PathType Leaf
$beforeDesktopExists=Test-Path $desktopShortcut -PathType Leaf
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
$afterArp=Assert-InstalledAppsRegistration
if([string]$afterArp.DisplayVersion -ne $beforeArpVersion -or [string]$afterArp.UninstallString -ne $beforeUninstallString){throw 'Upgrade rollback changed Installed Apps metadata.'}
if((Test-Path $startShortcut -PathType Leaf) -ne $beforeStartExists -or (Test-Path $desktopShortcut -PathType Leaf) -ne $beforeDesktopExists){throw 'Upgrade rollback changed shortcut state.'}
if(-not (Test-Path $upgradeSentinel -PathType Leaf)){throw 'Upgrade rollback lost ProgramData sentinel.'}
"rollback_upgrade_service_running=True" | Out-File $gateEvidence -Append
"rollback_upgrade_binary_restored=True" | Out-File $gateEvidence -Append
"rollback_upgrade_version_restored=True" | Out-File $gateEvidence -Append
"rollback_upgrade_installed_apps_restored=True" | Out-File $gateEvidence -Append
"rollback_upgrade_shortcuts_restored=True" | Out-File $gateEvidence -Append
"rollback_upgrade_programdata_preserved=True" | Out-File $gateEvidence -Append

$finalUninstall=Invoke-RegisteredQuietUninstall
if($finalUninstall.ExitCode -ne 0){throw "Final registered uninstaller returned $($finalUninstall.ExitCode)"}
if(Get-Service $service -ErrorAction SilentlyContinue){throw 'Service still exists after final rollback-gate uninstall.'}
if(Test-Path $uninstallRegPath){throw 'Installed Apps entry still exists after final rollback-gate uninstall.'}

"timestamp_end=$((Get-Date).ToUniversalTime().ToString('o'))" | Out-File $gateEvidence -Append
Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $setupLogRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $regPath,$uninstallRegPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'PASS Windows Setup/Service/RuntimePrerequisite/InstalledApps/Shortcut/Uninstall/Rollback/Repair smoke test' -ForegroundColor Green
