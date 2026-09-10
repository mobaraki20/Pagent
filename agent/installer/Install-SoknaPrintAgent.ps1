param(
  [string]$InstallRoot='',
  [string]$DataRoot='',
  [switch]$SkipStart,
  [switch]$CreateStartMenuShortcut,
  [switch]$NoStartMenuShortcut,
  [switch]$CreateDesktopShortcut,
  [switch]$NoDesktopShortcut
)
$ErrorActionPreference='Stop'
$service='SoknaPrintAgent6'
$regPath='HKLM:\SOFTWARE\Sokna\PrintAgent'
$uninstallRegPath='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SoknaPrintAgent6'
$script:InstallStage='setup_bootstrap_start'
$referenceId=[Guid]::NewGuid().ToString('N')
$systemSid='*S-1-5-18'
$administratorsSid='*S-1-5-32-544'
$usersSid='*S-1-5-32-545'

function Get-Sha256Hex([string]$Path){
  $sha=[Security.Cryptography.SHA256]::Create()
  $stream=$null
  try{
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $bytes=$sha.ComputeHash($stream)
    return ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()
  }
  finally{
    if($null -ne $stream){$stream.Dispose()}
    $sha.Dispose()
  }
}
function Set-InstallStage([string]$Name){
  $script:InstallStage=$Name
  Write-Output "SOKNA_SETUP_STAGE=$Name ref=$referenceId"
}
function Get-SafeMessage([string]$Text){
  if([string]::IsNullOrWhiteSpace($Text)){return 'unspecified'}
  $safe=($Text -split "`r?`n" | Where-Object {$_ -notmatch '(?i)authorization|bearer|token|secret|hmac'}) -join ' '
  if([string]::IsNullOrWhiteSpace($safe)){return 'redacted'}
  if($safe.Length -gt 900){return $safe.Substring(0,900)}
  return $safe
}
function Assert-NativeExit([string]$Operation){
  if($LASTEXITCODE -ne 0){throw "$Operation failed: $LASTEXITCODE"}
}
function Get-RegistrySnapshot([string]$Path){
  $exists=Test-Path $Path
  $values=@{}
  if($exists){
    $key=Get-Item $Path -ErrorAction Stop
    foreach($name in $key.GetValueNames()){
      if([string]::IsNullOrEmpty($name)){continue}
      $values[$name]=[pscustomobject]@{
        Value=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind=$key.GetValueKind($name).ToString()
      }
    }
  }
  return [pscustomobject]@{Exists=$exists;Values=$values}
}
function Restore-RegistrySnapshot([string]$Path,$Snapshot){
  if(-not $Snapshot.Exists){
    Remove-Item $Path -Recurse -Force -ErrorAction SilentlyContinue
    return
  }
  New-Item $Path -Force|Out-Null
  $key=Get-Item $Path -ErrorAction Stop
  foreach($name in $key.GetValueNames()){
    if(-not [string]::IsNullOrEmpty($name)){Remove-ItemProperty $Path -Name $name -Force -ErrorAction SilentlyContinue}
  }
  foreach($name in $Snapshot.Values.Keys){
    $entry=$Snapshot.Values[$name]
    New-ItemProperty $Path -Name $name -Value $entry.Value -PropertyType $entry.Kind -Force|Out-Null
  }
}
function Get-ShortcutSnapshot([string]$Path){
  if(-not (Test-Path $Path -PathType Leaf)){return [pscustomobject]@{Exists=$false;Target='';Working='';Icon='';Description=''}}
  $shortcut=$null
  $shell=New-Object -ComObject WScript.Shell
  try{
    $shortcut=$shell.CreateShortcut($Path)
    return [pscustomobject]@{
      Exists=$true
      Target=[string]$shortcut.TargetPath
      Working=[string]$shortcut.WorkingDirectory
      Icon=[string]$shortcut.IconLocation
      Description=[string]$shortcut.Description
    }
  }
  finally{
    if($null -ne $shortcut){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null}
    if($null -ne $shell){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null}
  }
}
function Restore-ShortcutSnapshot([string]$Path,$Snapshot){
  if(-not $Snapshot.Exists){Remove-Item $Path -Force -ErrorAction SilentlyContinue;return}
  $parent=Split-Path $Path -Parent
  New-Item $parent -ItemType Directory -Force|Out-Null
  $shortcut=$null
  $shell=New-Object -ComObject WScript.Shell
  try{
    $shortcut=$shell.CreateShortcut($Path)
    $shortcut.TargetPath=$Snapshot.Target
    $shortcut.WorkingDirectory=$Snapshot.Working
    $shortcut.IconLocation=$Snapshot.Icon
    $shortcut.Description=$Snapshot.Description
    $shortcut.Save()
  }
  finally{
    if($null -ne $shortcut){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null}
    if($null -ne $shell){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null}
  }
}
function Set-ProgramDataAcl([string]$Path){
  & icacls.exe $Path /inheritance:r /grant:r "$systemSid`:(OI)(CI)F" "$administratorsSid`:(OI)(CI)F" | Out-Null
  Assert-NativeExit 'icacls ProgramData'
}
function Set-ProgramFilesAcl([string]$Path){
  # Program binaries are immutable to standard users but must remain readable/executable so Explorer can
  # resolve the public shortcut/icon and the elevated Control app can be launched without manual ACL repair.
  & icacls.exe $Path /inheritance:r /grant:r "$systemSid`:(OI)(CI)RX" "$administratorsSid`:(OI)(CI)F" "$usersSid`:(OI)(CI)RX" | Out-Null
  Assert-NativeExit 'icacls Program Files'
}
function Move-DirectoryWithRetry([string]$Source,[string]$Destination,[int]$Attempts=8){
  $last=$null
  for($i=1;$i -le $Attempts;$i++){
    try{
      Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop
      return
    }
    catch{
      $last=$_
      if($i -ge $Attempts){break}
      Start-Sleep -Milliseconds ([Math]::Min(1500,200*$i))
    }
  }
  throw $last
}
function Stop-AgentProcesses(){
  foreach($name in @('Sokna.PrintAgent.Control','Sokna.PrintAgent.Worker')){
    Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  }
  $deadline=(Get-Date).AddSeconds(5)
  do{
    $remaining=@(Get-Process 'Sokna.PrintAgent.Control','Sokna.PrintAgent.Worker' -ErrorAction SilentlyContinue)
    if($remaining.Count -eq 0){return}
    Start-Sleep -Milliseconds 200
  }while((Get-Date) -lt $deadline)
  if($remaining.Count -gt 0){throw "Agent process did not exit before upgrade: $($remaining.ProcessName -join ', ')"}
}
function Get-ServiceStartupDiagnostic([string]$Root){
  $fatal=Join-Path $Root 'logs\startup-fatal.json'
  if(-not (Test-Path $fatal -PathType Leaf)){return 'startup diagnostic unavailable'}
  try{
    $d=Get-Content $fatal -Raw | ConvertFrom-Json
    $type=Get-SafeMessage ([string]$d.exception_type)
    $message=Get-SafeMessage ([string]$d.message)
    return "$type`: $message"
  }
  catch{return 'startup diagnostic unreadable'}
}
function Set-AgentShortcut([string]$Path,[string]$Target){
  if(-not (Test-Path $Target -PathType Leaf)){throw "Shortcut target is missing: $Target"}
  $parent=Split-Path $Path -Parent
  if([string]::IsNullOrWhiteSpace($parent)){throw "Shortcut parent path is empty: $Path"}
  New-Item $parent -ItemType Directory -Force|Out-Null
  $shortcut=$null
  $shell=New-Object -ComObject WScript.Shell
  try{
    $shortcut=$shell.CreateShortcut($Path)
    $shortcut.TargetPath=$Target
    $shortcut.WorkingDirectory=Split-Path $Target -Parent
    $shortcut.IconLocation="$Target,0"
    $shortcut.Description='Sokna Print Agent — Operations & Diagnostics Console'
    $shortcut.Save()
    if(-not (Test-Path $Path -PathType Leaf)){throw "Shortcut was not created: $Path"}
  }
  finally{
    if($null -ne $shortcut){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null}
    if($null -ne $shell){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null}
  }
}
function Set-InstalledAppsRegistration([string]$Version,[string]$Root){
  $control=Join-Path $Root 'Control\Sokna.PrintAgent.Control.exe'
  $uninstaller=Join-Path $Root 'Uninstall-SoknaPrintAgent.ps1'
  if(-not (Test-Path $control -PathType Leaf)){throw 'Installed Apps registration requires the Control executable.'}
  if(-not (Test-Path $uninstaller -PathType Leaf)){throw 'Installed Apps registration requires the uninstaller script.'}
  $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
  $uninstallString="`"$powershell`" -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""
  $quietUninstallString=$uninstallString+' -Quiet'
  $bytes=(Get-ChildItem $Root -File -Recurse -ErrorAction Stop | Measure-Object -Property Length -Sum).Sum
  if($null -eq $bytes){$bytes=0}
  $estimated=[int][Math]::Max(1,[Math]::Min([int]::MaxValue,[Math]::Ceiling([double]$bytes/1KB)))
  New-Item $uninstallRegPath -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name DisplayName -Value 'Sokna Print Agent' -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name DisplayVersion -Value $Version -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name Publisher -Value 'Sokna Group' -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name InstallLocation -Value $Root -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name DisplayIcon -Value "$control,0" -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name UninstallString -Value $uninstallString -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name QuietUninstallString -Value $quietUninstallString -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name NoModify -Value 1 -PropertyType DWord -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name NoRepair -Value 1 -PropertyType DWord -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name EstimatedSize -Value $estimated -PropertyType DWord -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name InstallDate -Value (Get-Date -Format 'yyyyMMdd') -PropertyType String -Force|Out-Null
  New-ItemProperty $uninstallRegPath -Name URLInfoAbout -Value 'https://www.soknagroup.ir/' -PropertyType String -Force|Out-Null
}
trap {
  $e=$_
  $type=if($e.Exception){$e.Exception.GetType().FullName}else{'PowerShell.ErrorRecord'}
  $message=Get-SafeMessage ([string]$e.Exception.Message)
  [Console]::Error.WriteLine("SOKNA_SETUP_FAILURE stage=$script:InstallStage ref=$referenceId type=$type message=$message")
  exit 1
}

Set-InstallStage 'elevation_admin_check'
$id=[Security.Principal.WindowsIdentity]::GetCurrent()
$p=New-Object Security.Principal.WindowsPrincipal($id)
if(-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Installer must run as Administrator.'}
if($CreateStartMenuShortcut -and $NoStartMenuShortcut){throw 'Conflicting Start Menu shortcut options.'}
if($CreateDesktopShortcut -and $NoDesktopShortcut){throw 'Conflicting Desktop shortcut options.'}

Set-InstallStage 'existing_install_lookup'
$configurationRegistrySnapshot=Get-RegistrySnapshot $regPath
$uninstallRegistrySnapshot=Get-RegistrySnapshot $uninstallRegPath
$oldRegExists=$configurationRegistrySnapshot.Exists
$oldInstallRoot=$null;$oldDataRoot=$null;$oldVersion=$null;$oldStartPreference=$null;$oldDesktopPreference=$null
if($oldRegExists){
  $old=Get-ItemProperty $regPath -ErrorAction SilentlyContinue
  $oldInstallRoot=$old.InstallRoot;$oldDataRoot=$old.DataRoot;$oldVersion=$old.Version
  if($old.PSObject.Properties.Name -contains 'CreateStartMenuShortcut'){$oldStartPreference=([int]$old.CreateStartMenuShortcut -ne 0)}
  if($old.PSObject.Properties.Name -contains 'CreateDesktopShortcut'){$oldDesktopPreference=([int]$old.CreateDesktopShortcut -ne 0)}
}

Set-InstallStage 'install_paths_resolution'
if([string]::IsNullOrWhiteSpace($InstallRoot)){$InstallRoot=if($oldInstallRoot){[string]$oldInstallRoot}else{"$env:ProgramFiles\Sokna\PrintAgent"}}
if([string]::IsNullOrWhiteSpace($DataRoot)){$DataRoot=if($oldDataRoot){[string]$oldDataRoot}else{"$env:ProgramData\Sokna\PrintAgent"}}
$InstallRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$DataRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))
if([string]::IsNullOrWhiteSpace($InstallRoot) -or [string]::IsNullOrWhiteSpace($DataRoot)){throw 'Resolved install/data path is empty.'}

$commonPrograms=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
$commonDesktop=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
if([string]::IsNullOrWhiteSpace($commonPrograms)){throw 'Common Start Menu path could not be resolved.'}
if([string]::IsNullOrWhiteSpace($commonDesktop)){throw 'Public Desktop path could not be resolved.'}
$startShortcutPath=Join-Path $commonPrograms 'Sokna Print Agent.lnk'
$desktopShortcutPath=Join-Path $commonDesktop 'Sokna Print Agent.lnk'
$startShortcutSnapshot=Get-ShortcutSnapshot $startShortcutPath
$desktopShortcutSnapshot=Get-ShortcutSnapshot $desktopShortcutPath

$wantStartMenu=if($CreateStartMenuShortcut){$true}elseif($NoStartMenuShortcut){$false}elseif($null -ne $oldStartPreference){[bool]$oldStartPreference}elseif($oldRegExists){[bool]$startShortcutSnapshot.Exists}else{$true}
$wantDesktop=if($CreateDesktopShortcut){$true}elseif($NoDesktopShortcut){$false}elseif($null -ne $oldDesktopPreference){[bool]$oldDesktopPreference}elseif($oldRegExists){[bool]$desktopShortcutSnapshot.Exists}else{$false}
Write-Host "Shortcut preference: StartMenu=$wantStartMenu Desktop=$wantDesktop"

Set-InstallStage 'embedded_payload_presence'
$source=Join-Path $PSScriptRoot 'payload'
$manifestPath=Join-Path $PSScriptRoot 'PAYLOAD_MANIFEST.json'
$versionPath=Join-Path $PSScriptRoot 'VERSION.txt'
if(-not (Test-Path (Join-Path $source 'Service\Sokna.PrintAgent.Service.exe') -PathType Leaf)){throw 'Agent binaries are missing. Build the package first.'}
if(-not (Test-Path $manifestPath -PathType Leaf)){throw 'PAYLOAD_MANIFEST.json is missing.'}
if(-not (Test-Path $versionPath -PathType Leaf)){throw 'VERSION.txt is missing.'}
$version=(Get-Content $versionPath -Raw).Trim()
if($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$'){throw 'VERSION.txt is invalid.'}

Set-InstallStage 'payload_manifest_hash_validation'
$packageRoot=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$manifest=Get-Content $manifestPath -Raw | ConvertFrom-Json
if($null -eq $manifest){throw 'PAYLOAD_MANIFEST.json is empty.'}
foreach($entry in @($manifest)){
  $rel=([string]$entry.path).Replace('/','\')
  if([string]::IsNullOrWhiteSpace($rel) -or [IO.Path]::IsPathRooted($rel)){throw 'Payload manifest contains an invalid path.'}
  $file=[IO.Path]::GetFullPath((Join-Path $packageRoot $rel))
  $prefix=$packageRoot+'\'
  if(-not $file.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Payload manifest path escapes package root.'}
  if(-not (Test-Path $file -PathType Leaf)){throw "Payload file missing: $rel"}
  if((Get-Item $file).Length -ne [int64]$entry.size){throw "Payload size mismatch: $rel"}
  $actual=Get-Sha256Hex $file
  if($actual -ne ([string]$entry.sha256).ToLowerInvariant()){throw "Payload SHA256 mismatch: $rel"}
}

Set-InstallStage 'programdata_setup'
$parent=Split-Path $InstallRoot -Parent
New-Item $parent -ItemType Directory -Force|Out-Null
New-Item $DataRoot -ItemType Directory -Force|Out-Null
New-Item (Join-Path $DataRoot 'logs') -ItemType Directory -Force|Out-Null
New-Item (Join-Path $DataRoot 'work') -ItemType Directory -Force|Out-Null

Set-InstallStage 'programdata_acl'
Set-ProgramDataAcl $DataRoot

$stage="$InstallRoot.__stage_$([Guid]::NewGuid().ToString('N'))"
$backup="$InstallRoot.__backup_$([Guid]::NewGuid().ToString('N'))"
$hadPrevious=Test-Path $InstallRoot
$previousService=Get-Service $service -ErrorAction SilentlyContinue
$installedNew=$false
$registryChanged=$false
$shortcutsChanged=$false
$uninstallRegistryChanged=$false

try{
  if($hadPrevious){
    Set-InstallStage 'existing_install_acl_repair'
    Set-ProgramFilesAcl $InstallRoot
  }

  Set-InstallStage 'program_files_parent_preflight'
  $parentProbe=Join-Path $parent ('.sokna-install-probe-'+[Guid]::NewGuid().ToString('N'))
  try{New-Item $parentProbe -ItemType Directory -ErrorAction Stop|Out-Null}finally{Remove-Item $parentProbe -Recurse -Force -ErrorAction SilentlyContinue}

  Set-InstallStage 'payload_copy'
  New-Item $stage -ItemType Directory -Force|Out-Null
  Copy-Item (Join-Path $source '*') $stage -Recurse -Force

  Set-InstallStage 'program_files_layout_validation'
  foreach($required in @(
    'Service\Sokna.PrintAgent.Service.exe',
    'Worker\Sokna.PrintAgent.Worker.exe',
    'Worker\Fonts\Vazirmatn-Regular.ttf',
    'Worker\Fonts\Vazirmatn-Bold.ttf',
    'Worker\Fonts\OFL.txt',
    'Control\Sokna.PrintAgent.Control.exe',
    'Uninstall-SoknaPrintAgent.ps1'
  )){
    if(-not (Test-Path (Join-Path $stage $required) -PathType Leaf)){throw "Staged component is missing: $required"}
  }

  Set-InstallStage 'program_files_acl'
  Set-ProgramFilesAcl $stage

  Set-InstallStage 'previous_service_handling'
  Stop-AgentProcesses
  if($previousService){
    Stop-Service $service -Force -ErrorAction Stop
    $stopDeadline=(Get-Date).AddSeconds(15)
    do{
      Start-Sleep -Milliseconds 250
      $stopped=Get-Service $service -ErrorAction Stop
    }while($stopped.Status -ne 'Stopped' -and (Get-Date) -lt $stopDeadline)
    if($stopped.Status -ne 'Stopped'){throw "Previous Service did not reach Stopped state: $($stopped.Status)"}
  }
  Stop-AgentProcesses

  Set-InstallStage 'program_files_swap'
  if($hadPrevious){Move-DirectoryWithRetry $InstallRoot $backup}
  Move-DirectoryWithRetry $stage $InstallRoot
  $installedNew=$true
  Set-ProgramFilesAcl $InstallRoot

  Set-InstallStage 'configuration_registry'
  $registryChanged=$true
  New-Item $regPath -Force|Out-Null
  New-ItemProperty $regPath -Name InstallRoot -Value $InstallRoot -PropertyType String -Force|Out-Null
  New-ItemProperty $regPath -Name DataRoot -Value $DataRoot -PropertyType String -Force|Out-Null
  New-ItemProperty $regPath -Name Version -Value $version -PropertyType String -Force|Out-Null
  New-ItemProperty $regPath -Name CreateStartMenuShortcut -Value ([int]$wantStartMenu) -PropertyType DWord -Force|Out-Null
  New-ItemProperty $regPath -Name CreateDesktopShortcut -Value ([int]$wantDesktop) -PropertyType DWord -Force|Out-Null

  Set-InstallStage 'service_create_or_config'
  $exe=Join-Path $InstallRoot 'Service\Sokna.PrintAgent.Service.exe'
  if(-not (Get-Service $service -ErrorAction SilentlyContinue)){
    & sc.exe create $service binPath= "`"$exe`"" start= delayed-auto obj= LocalSystem DisplayName= "Sokna Print Agent" | Out-Null
    Assert-NativeExit 'sc create'
  }else{
    & sc.exe config $service binPath= "`"$exe`"" start= delayed-auto obj= LocalSystem DisplayName= "Sokna Print Agent" | Out-Null
    Assert-NativeExit 'sc config'
  }

  Set-InstallStage 'automatic_delayed_start_validation'
  $svcReg="HKLM:\SYSTEM\CurrentControlSet\Services\$service"
  $svcProps=Get-ItemProperty $svcReg -ErrorAction Stop
  if([int]$svcProps.Start -ne 2){throw 'Service start type is not Automatic.'}
  if([int]$svcProps.DelayedAutoStart -ne 1){throw 'Service is not configured for Automatic Delayed Start.'}

  Set-InstallStage 'service_recovery'
  & sc.exe failure $service reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
  Assert-NativeExit 'sc failure'
  & sc.exe failureflag $service 1 | Out-Null
  Assert-NativeExit 'sc failureflag'
  $failureConfig=& sc.exe qfailure $service | Out-String
  Assert-NativeExit 'sc qfailure'
  if($failureConfig -notmatch 'RESTART'){throw 'Service Recovery restart action is missing.'}

  Set-InstallStage 'bundled_font_validation'
  foreach($font in @('Vazirmatn-Regular.ttf','Vazirmatn-Bold.ttf','OFL.txt')){
    if(-not (Test-Path (Join-Path $InstallRoot "Worker\Fonts\$font") -PathType Leaf)){throw "Bundled font asset is missing: $font"}
  }

  if(-not $SkipStart){
    Set-InstallStage 'service_start'
    $health=Join-Path $DataRoot 'health.json'
    $startupFatal=Join-Path $DataRoot 'logs\startup-fatal.json'
    Remove-Item $health,$startupFatal -Force -ErrorAction SilentlyContinue
    try{Start-Service $service -ErrorAction Stop}
    catch{
      Start-Sleep -Milliseconds 500
      $diag=Get-ServiceStartupDiagnostic $DataRoot
      throw "Failed to start Service. diagnostic=$diag original=$(Get-SafeMessage ([string]$_.Exception.Message))"
    }
    $deadline=(Get-Date).AddSeconds(20)
    $s=Get-Service $service -ErrorAction Stop
    do{
      Start-Sleep -Milliseconds 500
      $s=Get-Service $service -ErrorAction Stop
      if($s.Status -eq 'Stopped'){
        $diag=Get-ServiceStartupDiagnostic $DataRoot
        throw "Service stopped during startup. diagnostic=$diag"
      }
    }while((($s.Status -ne 'Running') -or (-not (Test-Path $health))) -and (Get-Date) -lt $deadline)
    if($s.Status -ne 'Running'){
      $diag=Get-ServiceStartupDiagnostic $DataRoot
      throw "Service did not reach Running state: $($s.Status). diagnostic=$diag"
    }

    Set-InstallStage 'health_json'
    if(-not (Test-Path $health -PathType Leaf)){throw 'Service is running but a fresh health.json was not produced within 20 seconds.'}
    $snapshot=Get-Content $health -Raw | ConvertFrom-Json
    if(-not $snapshot.updated_at){throw 'health.json is incomplete.'}
    Write-Host "Service health: $($snapshot.state) | service-account-context=$($snapshot.service_account_context)"
    Write-Host 'Printer queues visible to the Service account:'
    if($null -eq $snapshot.printers -or $snapshot.printers.Count -eq 0){Write-Warning 'No printer queue is visible to LocalSystem. Install/map a machine-wide or Standard TCP/IP printer before Production use.'}
    else{$snapshot.printers | Select-Object name,offline,paused,paper_out,error,jobs,driver,port | Format-Table -AutoSize}
  }

  Set-InstallStage 'component_path_validation'
  foreach($required in @(
    'Service\Sokna.PrintAgent.Service.exe',
    'Worker\Sokna.PrintAgent.Worker.exe',
    'Worker\Fonts\Vazirmatn-Regular.ttf',
    'Worker\Fonts\Vazirmatn-Bold.ttf',
    'Worker\Fonts\OFL.txt',
    'Control\Sokna.PrintAgent.Control.exe',
    'Uninstall-SoknaPrintAgent.ps1'
  )){
    if(-not (Test-Path (Join-Path $InstallRoot $required) -PathType Leaf)){throw "Installed component is missing: $required"}
  }

  Set-InstallStage 'shortcut_registration'
  $shortcutsChanged=$true
  $control=Join-Path $InstallRoot 'Control\Sokna.PrintAgent.Control.exe'
  if($wantStartMenu){Set-AgentShortcut $startShortcutPath $control}else{Remove-Item $startShortcutPath -Force -ErrorAction SilentlyContinue}
  if($wantDesktop){Set-AgentShortcut $desktopShortcutPath $control}else{Remove-Item $desktopShortcutPath -Force -ErrorAction SilentlyContinue}
  Write-Host "Shortcuts applied: StartMenu=$wantStartMenu Desktop=$wantDesktop"

  Set-InstallStage 'installed_apps_registration'
  $uninstallRegistryChanged=$true
  Set-InstalledAppsRegistration $version $InstallRoot

  Set-InstallStage 'finalize'
  if(Test-Path $backup){
    try{Remove-Item $backup -Recurse -Force -ErrorAction Stop}
    catch{Write-Warning "Backup cleanup deferred: $(Get-SafeMessage ([string]$_.Exception.Message))"}
  }
  Write-Host "Installed/Upgraded Sokna Print Agent $version. Durable data preserved at: $DataRoot" -ForegroundColor Green
  Write-Host "Windows Installed Apps registration is active. Shortcuts: StartMenu=$wantStartMenu Desktop=$wantDesktop."
}
catch{
  $failure=$_
  Write-Warning "Install/upgrade failed; recovering previous installation state: $(Get-SafeMessage ([string]$failure.Exception.Message))"
  $recoveryFailure=$null
  try{
    try{Get-Service $service -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue}catch{}
    Stop-AgentProcesses
    if($installedNew -and (Test-Path $InstallRoot)){Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop}
    if(Test-Path $backup){Move-DirectoryWithRetry $backup $InstallRoot}
    if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
    if(Test-Path $InstallRoot){Set-ProgramFilesAcl $InstallRoot}

    if($registryChanged){Restore-RegistrySnapshot $regPath $configurationRegistrySnapshot}
    if($uninstallRegistryChanged){Restore-RegistrySnapshot $uninstallRegPath $uninstallRegistrySnapshot}
    if($shortcutsChanged){
      Restore-ShortcutSnapshot $startShortcutPath $startShortcutSnapshot
      Restore-ShortcutSnapshot $desktopShortcutPath $desktopShortcutSnapshot
    }

    if($null -ne $previousService){
      $oldExe=Join-Path $InstallRoot 'Service\Sokna.PrintAgent.Service.exe'
      if(-not (Test-Path $oldExe -PathType Leaf)){$oldExe=Join-Path $InstallRoot 'Sokna.PrintAgent.Service.exe'}
      if(-not (Test-Path $oldExe -PathType Leaf)){throw 'Previous Service binary could not be restored.'}
      & sc.exe config $service binPath= "`"$oldExe`"" start= delayed-auto obj= LocalSystem | Out-Null
      Assert-NativeExit 'sc previous service restore'
      Start-Service $service -ErrorAction Stop
      $restoreDeadline=(Get-Date).AddSeconds(15)
      do{
        Start-Sleep -Milliseconds 300
        $restoredService=Get-Service $service -ErrorAction Stop
      }while($restoredService.Status -ne 'Running' -and (Get-Date) -lt $restoreDeadline)
      if($restoredService.Status -ne 'Running'){throw "Previous Service did not return to Running state: $($restoredService.Status)"}
    }
    else{
      $newService=Get-Service $service -ErrorAction SilentlyContinue
      if($newService){
        & sc.exe delete $service | Out-Null
        Assert-NativeExit 'sc orphan service cleanup'
        $deleteDeadline=(Get-Date).AddSeconds(15)
        do{
          Start-Sleep -Milliseconds 300
          $newService=Get-Service $service -ErrorAction SilentlyContinue
        }while($newService -and (Get-Date) -lt $deleteDeadline)
        if($newService){throw 'New Service remained registered after failed fresh install recovery.'}
      }
    }

    Write-Output "SOKNA_ROLLBACK_RESULT=success ref=$referenceId"
  }
  catch{
    $recoveryFailure=$_
    $recoveryMessage=Get-SafeMessage ([string]$recoveryFailure.Exception.Message)
    [Console]::Error.WriteLine("SOKNA_RECOVERY_FAILURE ref=$referenceId message=$recoveryMessage")
  }

  if($null -ne $recoveryFailure){
    $originalMessage=Get-SafeMessage ([string]$failure.Exception.Message)
    $recoveryMessage=Get-SafeMessage ([string]$recoveryFailure.Exception.Message)
    throw "Install/upgrade failed and previous-state recovery could not be verified. original=$originalMessage recovery=$recoveryMessage"
  }
  throw $failure
}
finally{
  if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
}
