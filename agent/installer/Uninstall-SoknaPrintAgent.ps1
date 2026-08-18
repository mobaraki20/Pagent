param(
  [string]$InstallRoot='',
  [string]$DataRoot='',
  [switch]$RemoveData
)
$ErrorActionPreference='Stop'
$service='SoknaPrintAgent6'
$regPath='HKLM:\SOFTWARE\Sokna\PrintAgent'
$id=[Security.Principal.WindowsIdentity]::GetCurrent()
$p=New-Object Security.Principal.WindowsPrincipal($id)
if(-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Uninstaller must run as Administrator.'}

$reg=if(Test-Path $regPath){Get-ItemProperty $regPath -ErrorAction SilentlyContinue}else{$null}
if([string]::IsNullOrWhiteSpace($InstallRoot)){$InstallRoot=if($null -ne $reg -and $reg.InstallRoot){[string]$reg.InstallRoot}else{"$env:ProgramFiles\Sokna\PrintAgent"}}
if([string]::IsNullOrWhiteSpace($DataRoot)){$DataRoot=if($null -ne $reg -and $reg.DataRoot){[string]$reg.DataRoot}else{"$env:ProgramData\Sokna\PrintAgent"}}
$InstallRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$DataRoot=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))

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

if(Test-Path $InstallRoot){Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop}
Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue

if($RemoveData){
  $answer=Read-Host 'Type DELETE to remove config, logs and SQLite'
  if($answer -eq 'DELETE'){
    Remove-Item $DataRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'ProgramData removed by explicit operator request.'
  }else{Write-Host "Data preserved at $DataRoot"}
}else{Write-Host "Data preserved at $DataRoot"}
