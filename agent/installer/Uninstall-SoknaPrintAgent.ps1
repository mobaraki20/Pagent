param(
  [string]$InstallRoot='',
  [string]$DataRoot='',
  [switch]$RemoveData,
  [switch]$Quiet,
  [switch]$NoSelfElevation
)
$ErrorActionPreference='Stop'
$service='SoknaPrintAgent6'
$regPath='HKLM:\SOFTWARE\Sokna\PrintAgent'
$uninstallRegPath='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SoknaPrintAgent6'

function Test-IsAdministrator(){
  $id=[Security.Principal.WindowsIdentity]::GetCurrent()
  $p=New-Object Security.Principal.WindowsPrincipal($id)
  return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if(-not (Test-IsAdministrator)){
  if($NoSelfElevation){throw 'Uninstaller requires Administrator privileges.'}
  if($RemoveData -and $Quiet){throw 'Quiet uninstall cannot remove ProgramData because destructive data removal requires explicit operator confirmation.'}
  if([string]::IsNullOrWhiteSpace($PSCommandPath)){throw 'Uninstaller path is unavailable for elevation.'}
  if($PSCommandPath.Contains('"') -or $InstallRoot.Contains('"') -or $DataRoot.Contains('"')){throw 'Uninstaller path contains an unsupported quote character.'}
  $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
  $arguments="-NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -NoSelfElevation"
  if(-not [string]::IsNullOrWhiteSpace($InstallRoot)){$arguments+=" -InstallRoot `"$InstallRoot`""}
  if(-not [string]::IsNullOrWhiteSpace($DataRoot)){$arguments+=" -DataRoot `"$DataRoot`""}
  if($RemoveData){$arguments+=' -RemoveData'}
  if($Quiet){$arguments+=' -Quiet'}
  try{
    $elevated=Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
  }
  catch{
    if(-not $Quiet){Write-Error $_.Exception.Message}
    exit 1
  }
}

if($RemoveData -and $Quiet){throw 'Quiet uninstall cannot remove ProgramData because destructive data removal requires explicit operator confirmation.'}

$reg=if(Test-Path $regPath){Get-ItemProperty $regPath -ErrorAction SilentlyContinue}else{$null}
if([string]::IsNullOrWhiteSpace($InstallRoot)){$InstallRoot=if($null -ne $reg -and $reg.InstallRoot){[string]$reg.InstallRoot}else{"$env:ProgramFiles\Sokna\PrintAgent"}}
if([string]::IsNullOrWhiteSpace($DataRoot)){$DataRoot=if($null -ne $reg -and $reg.DataRoot){[string]$reg.DataRoot}else{"$env:ProgramData\Sokna\PrintAgent"}}
$InstallRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$DataRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))

foreach($name in @('Sokna.PrintAgent.Control','Sokna.PrintAgent.Worker')){
  Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$svc=Get-Service $service -ErrorAction SilentlyContinue
if($svc){
  Stop-Service $service -Force -ErrorAction SilentlyContinue
  $stopDeadline=(Get-Date).AddSeconds(15)
  do{
    Start-Sleep -Milliseconds 250
    $svc=Get-Service $service -ErrorAction SilentlyContinue
  }while($svc -and $svc.Status -ne 'Stopped' -and (Get-Date) -lt $stopDeadline)
  if($svc -and $svc.Status -ne 'Stopped'){throw 'Service did not stop during uninstall.'}

  & sc.exe delete $service | Out-Null
  if($LASTEXITCODE -ne 0){throw "sc delete failed: $LASTEXITCODE"}
  $deleteDeadline=(Get-Date).AddSeconds(15)
  do{
    Start-Sleep -Milliseconds 250
    $svc=Get-Service $service -ErrorAction SilentlyContinue
  }while($svc -and (Get-Date) -lt $deleteDeadline)
  if($svc){throw 'Service is still registered after sc delete.'}
}

$commonPrograms=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
$commonDesktop=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
foreach($shortcut in @(
  (Join-Path $commonPrograms 'Sokna Print Agent.lnk'),
  (Join-Path $commonDesktop 'Sokna Print Agent.lnk')
)){
  Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
}

# Remove Windows Installed Apps metadata before deleting the script that owns this operation.
Remove-Item $uninstallRegPath -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
if(Test-Path $InstallRoot){Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop}

if($RemoveData){
  $answer=Read-Host 'Type DELETE to remove config, logs, test PDFs and SQLite'
  if($answer -eq 'DELETE'){
    Remove-Item $DataRoot -Recurse -Force -ErrorAction SilentlyContinue
    if(-not $Quiet){Write-Host 'ProgramData removed by explicit operator request.'}
  }elseif(-not $Quiet){Write-Host "Data preserved at $DataRoot"}
}elseif(-not $Quiet){
  Write-Host "Application removed. Durable ProgramData preserved at $DataRoot"
}
