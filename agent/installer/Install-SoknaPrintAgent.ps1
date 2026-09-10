param(
  [string]$InstallRoot='',
  [string]$DataRoot='',
  [switch]$SkipStart
)
$ErrorActionPreference='Stop'
$service='SoknaPrintAgent6'
$regPath='HKLM:\SOFTWARE\Sokna\PrintAgent'
$script:InstallStage='setup_bootstrap_start'
$referenceId=[Guid]::NewGuid().ToString('N')
$shortcutCleanupCandidates=@()
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

Set-InstallStage 'existing_install_lookup'
$oldRegExists=Test-Path $regPath
$oldInstallRoot=$null;$oldDataRoot=$null;$oldVersion=$null
if($oldRegExists){
  $old=Get-ItemProperty $regPath -ErrorAction SilentlyContinue
  $oldInstallRoot=$old.InstallRoot;$oldDataRoot=$old.DataRoot;$oldVersion=$old.Version
}

Set-InstallStage 'install_paths_resolution'
if([string]::IsNullOrWhiteSpace($InstallRoot)){$InstallRoot=if($oldInstallRoot){[string]$oldInstallRoot}else{"$env:ProgramFiles\Sokna\PrintAgent"}}
if([string]::IsNullOrWhiteSpace($DataRoot)){$DataRoot=if($oldDataRoot){[string]$oldDataRoot}else{"$env:ProgramData\Sokna\PrintAgent"}}
$InstallRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$DataRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))
if([string]::IsNullOrWhiteSpace($InstallRoot) -or [string]::IsNullOrWhiteSpace($DataRoot)){throw 'Resolved install/data path is empty.'}

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
  New-Item $regPath -Force|Out-Null
  New-ItemProperty $regPath -Name InstallRoot -Value $InstallRoot -PropertyType String -Force|Out-Null
  New-ItemProperty $regPath -Name DataRoot -Value $DataRoot -PropertyType String -Force|Out-Null
  New-ItemProperty $regPath -Name Version -Value $version -PropertyType String -Force|Out-Null
  $registryChanged=$true

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
  $control=Join-Path $InstallRoot 'Control\Sokna.PrintAgent.Control.exe'
  $commonPrograms=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
  $commonDesktop=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
  if([string]::IsNullOrWhiteSpace($commonPrograms)){throw 'Common Start Menu path could not be resolved.'}
  if([string]::IsNullOrWhiteSpace($commonDesktop)){throw 'Public Desktop path could not be resolved.'}
  $shortcutPaths=@(
    (Join-Path $commonPrograms 'Sokna Print Agent.lnk')
    (Join-Path $commonDesktop 'Sokna Print Agent.lnk')
  )
  foreach($shortcutPath in $shortcutPaths){
    if(-not (Test-Path $shortcutPath -PathType Leaf)){$shortcutCleanupCandidates += $shortcutPath}
    Set-AgentShortcut $shortcutPath $control
  }

  Set-InstallStage 'finalize'
  if(Test-Path $backup){Remove-Item $backup -Recurse -Force}
  Write-Host "Installed/Upgraded Sokna Print Agent $version. Durable data preserved at: $DataRoot" -ForegroundColor Green
  Write-Host 'Operations Console is available from Start Menu/Desktop. Service reloads configuration automatically; restart is not required.'
}
catch{
  $failure=$_
  Write-Warning "Install/upgrade failed; recovering previous installation state: $(Get-SafeMessage ([string]$failure.Exception.Message))"
  $recoveryFailure=$null
  try{
    try{Get-Service $service -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue}catch{}
    Stop-AgentProcesses
    foreach($shortcutPath in $shortcutCleanupCandidates){Remove-Item $shortcutPath -Force -ErrorAction SilentlyContinue}
    if($installedNew -and (Test-Path $InstallRoot)){Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop}
    if(Test-Path $backup){Move-DirectoryWithRetry $backup $InstallRoot}
    if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
    if(Test-Path $InstallRoot){Set-ProgramFilesAcl $InstallRoot}

    if($registryChanged){
      if($oldRegExists){
        New-Item $regPath -Force|Out-Null
        if($null -ne $oldInstallRoot){New-ItemProperty $regPath -Name InstallRoot -Value ([string]$oldInstallRoot) -PropertyType String -Force|Out-Null}else{Remove-ItemProperty $regPath -Name InstallRoot -ErrorAction SilentlyContinue}
        if($null -ne $oldDataRoot){New-ItemProperty $regPath -Name DataRoot -Value ([string]$oldDataRoot) -PropertyType String -Force|Out-Null}else{Remove-ItemProperty $regPath -Name DataRoot -ErrorAction SilentlyContinue}
        if($null -ne $oldVersion){New-ItemProperty $regPath -Name Version -Value ([string]$oldVersion) -PropertyType String -Force|Out-Null}else{Remove-ItemProperty $regPath -Name Version -ErrorAction SilentlyContinue}
      }else{
        Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
      }
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
