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
$oldInstallRoot=$null;$oldDataRoot=$null
if($oldRegExists){$old=Get-ItemProperty $regPath -ErrorAction SilentlyContinue;$oldInstallRoot=$old.InstallRoot;$oldDataRoot=$old.DataRoot}

Set-InstallStage 'install_paths_resolution'
if([string]::IsNullOrWhiteSpace($InstallRoot)){$InstallRoot=if($oldInstallRoot){[string]$oldInstallRoot}else{"$env:ProgramFiles\Sokna\PrintAgent"}}
if([string]::IsNullOrWhiteSpace($DataRoot)){$DataRoot=if($oldDataRoot){[string]$oldDataRoot}else{"$env:ProgramData\Sokna\PrintAgent"}}
$InstallRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$DataRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))
if([string]::IsNullOrWhiteSpace($InstallRoot) -or [string]::IsNullOrWhiteSpace($DataRoot)){throw 'Resolved install/data path is empty.'}

Set-InstallStage 'embedded_payload_presence'
$source=Join-Path $PSScriptRoot 'payload'
$manifestPath=Join-Path $PSScriptRoot 'PAYLOAD_MANIFEST.json'
if(-not (Test-Path (Join-Path $source 'Service\Sokna.PrintAgent.Service.exe') -PathType Leaf)){throw 'Agent binaries are missing. Build the package first.'}
if(-not (Test-Path $manifestPath -PathType Leaf)){throw 'PAYLOAD_MANIFEST.json is missing.'}

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
  $actual=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
  if($actual -ne ([string]$entry.sha256).ToLowerInvariant()){throw "Payload SHA256 mismatch: $rel"}
}

Set-InstallStage 'programdata_setup'
$parent=Split-Path $InstallRoot -Parent
New-Item $parent -ItemType Directory -Force|Out-Null
New-Item $DataRoot -ItemType Directory -Force|Out-Null
New-Item (Join-Path $DataRoot 'logs') -ItemType Directory -Force|Out-Null
New-Item (Join-Path $DataRoot 'work') -ItemType Directory -Force|Out-Null

Set-InstallStage 'programdata_acl'
& icacls $DataRoot /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
Assert-NativeExit 'icacls ProgramData'

$stage="$InstallRoot.__stage_$([Guid]::NewGuid().ToString('N'))"
$backup="$InstallRoot.__backup_$([Guid]::NewGuid().ToString('N'))"
$hadPrevious=Test-Path $InstallRoot
$previousService=Get-Service $service -ErrorAction SilentlyContinue
$installedNew=$false
$registryChanged=$false

try{
  Set-InstallStage 'payload_copy'
  New-Item $stage -ItemType Directory -Force|Out-Null
  Copy-Item (Join-Path $source '*') $stage -Recurse -Force

  Set-InstallStage 'program_files_layout_validation'
  foreach($required in @(
    'Service\Sokna.PrintAgent.Service.exe',
    'Worker\Sokna.PrintAgent.Worker.exe',
    'Control\Sokna.PrintAgent.Control.exe',
    'Uninstall-SoknaPrintAgent.ps1'
  )){
    if(-not (Test-Path (Join-Path $stage $required) -PathType Leaf)){throw "Staged component is missing: $required"}
  }

  Set-InstallStage 'program_files_acl'
  & icacls $stage /inheritance:r /grant:r 'SYSTEM:(OI)(CI)RX' 'Administrators:(OI)(CI)F' | Out-Null
  Assert-NativeExit 'icacls staged Program Files'

  Set-InstallStage 'previous_service_handling'
  Get-Process 'Sokna.PrintAgent.Control' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  if($previousService){Stop-Service $service -Force -ErrorAction Stop}

  Set-InstallStage 'program_files_swap'
  if($hadPrevious){Move-Item $InstallRoot $backup}
  Move-Item $stage $InstallRoot
  $installedNew=$true
  & icacls $InstallRoot /inheritance:r /grant:r 'SYSTEM:(OI)(CI)RX' 'Administrators:(OI)(CI)F' | Out-Null
  Assert-NativeExit 'icacls installed Program Files'

  Set-InstallStage 'configuration_registry'
  New-Item $regPath -Force|Out-Null
  New-ItemProperty $regPath -Name InstallRoot -Value $InstallRoot -PropertyType String -Force|Out-Null
  New-ItemProperty $regPath -Name DataRoot -Value $DataRoot -PropertyType String -Force|Out-Null
  $registryChanged=$true

  Set-InstallStage 'service_create_or_config'
  $exe=Join-Path $InstallRoot 'Service\Sokna.PrintAgent.Service.exe'
  if(-not (Get-Service $service -ErrorAction SilentlyContinue)){
    & sc.exe create $service binPath= "`"$exe`"" start= delayed-auto obj= LocalSystem DisplayName= "Sokna Print Agent 6" | Out-Null
    Assert-NativeExit 'sc create'
  }else{
    & sc.exe config $service binPath= "`"$exe`"" start= delayed-auto obj= LocalSystem | Out-Null
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

  Set-InstallStage 'machine_font_check'
  $machineFont=$false
  foreach($fontKey in @('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Fonts')){
    if(Test-Path $fontKey){$props=Get-ItemProperty $fontKey;foreach($name in $props.PSObject.Properties.Name){if($name -like 'Vazirmatn*'){$machineFont=$true;break}}}
    if($machineFont){break}
  }
  if(-not $machineFont){Write-Warning 'Vazirmatn is not installed machine-wide; deterministic Tahoma/Segoe UI fallback will be used.'}

  if(-not $SkipStart){
    Set-InstallStage 'service_start'
    $health=Join-Path $DataRoot 'health.json'
    Remove-Item $health -Force -ErrorAction SilentlyContinue
    Start-Service $service
    $deadline=(Get-Date).AddSeconds(20)
    do{
      Start-Sleep -Milliseconds 500
      $s=Get-Service $service
      if($s.Status -ne 'Running'){throw "Service did not stay running: $($s.Status)"}
    }while((-not (Test-Path $health)) -and (Get-Date) -lt $deadline)

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
    'Control\Sokna.PrintAgent.Control.exe',
    'Uninstall-SoknaPrintAgent.ps1'
  )){
    if(-not (Test-Path (Join-Path $InstallRoot $required) -PathType Leaf)){throw "Installed component is missing: $required"}
  }

  Set-InstallStage 'finalize'
  if(Test-Path $backup){Remove-Item $backup -Recurse -Force}
  Write-Host "Installed/Upgraded. Durable data preserved at: $DataRoot" -ForegroundColor Green
  Write-Host 'Open Control\Sokna.PrintAgent.Control.exe as Administrator to save URL/token and test API v4. Service reload is automatic; restart is not required.'
}
catch{
  $failure=$_
  Write-Warning "Install/upgrade failed; attempting binary/configuration-location rollback: $(Get-SafeMessage ([string]$failure.Exception.Message))"
  try{Get-Service $service -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue}catch{}
  if($installedNew -and (Test-Path $InstallRoot)){Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue}
  if(Test-Path $backup){Move-Item $backup $InstallRoot -Force}
  if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
  if($registryChanged){
    if($oldRegExists){
      New-Item $regPath -Force|Out-Null
      if($null -ne $oldInstallRoot){New-ItemProperty $regPath -Name InstallRoot -Value ([string]$oldInstallRoot) -PropertyType String -Force|Out-Null}else{Remove-ItemProperty $regPath -Name InstallRoot -ErrorAction SilentlyContinue}
      if($null -ne $oldDataRoot){New-ItemProperty $regPath -Name DataRoot -Value ([string]$oldDataRoot) -PropertyType String -Force|Out-Null}else{Remove-ItemProperty $regPath -Name DataRoot -ErrorAction SilentlyContinue}
    }else{Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue}
  }
  if($hadPrevious){
    $oldExe=Join-Path $InstallRoot 'Service\Sokna.PrintAgent.Service.exe'
    if(-not (Test-Path $oldExe -PathType Leaf)){$oldExe=Join-Path $InstallRoot 'Sokna.PrintAgent.Service.exe'}
    if(Test-Path $oldExe -PathType Leaf){
      try{& sc.exe config $service binPath= "`"$oldExe`"" start= delayed-auto obj= LocalSystem | Out-Null;Start-Service $service -ErrorAction SilentlyContinue}catch{}
    }
  }
  throw $failure
}
finally{
  if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
}
