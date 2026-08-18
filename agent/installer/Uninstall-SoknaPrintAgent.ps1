param([string]$InstallRoot='',[string]$DataRoot='',[switch]$RemoveData)
$ErrorActionPreference='Stop';$service='SoknaPrintAgent6';$regPath='HKLM:\SOFTWARE\Sokna\PrintAgent'
$id=[Security.Principal.WindowsIdentity]::GetCurrent();$p=New-Object Security.Principal.WindowsPrincipal($id);if(-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Uninstaller must run as Administrator.'}
$reg=if(Test-Path $regPath){Get-ItemProperty $regPath -ErrorAction SilentlyContinue}else{$null}
if([string]::IsNullOrWhiteSpace($InstallRoot)){$InstallRoot=if($reg.InstallRoot){[string]$reg.InstallRoot}else{"$env:ProgramFiles\Sokna\PrintAgent"}}
if([string]::IsNullOrWhiteSpace($DataRoot)){$DataRoot=if($reg.DataRoot){[string]$reg.DataRoot}else{"$env:ProgramData\Sokna\PrintAgent"}}
Get-Service $service -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue
if(Get-Service $service -ErrorAction SilentlyContinue){& sc.exe delete $service | Out-Null}
Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
if($RemoveData){$answer=Read-Host "Type DELETE to remove config, logs and SQLite";if($answer -eq 'DELETE'){Remove-Item $DataRoot -Recurse -Force -ErrorAction SilentlyContinue;Write-Host 'ProgramData removed by explicit operator request.'}else{Write-Host 'Data preserved.'}}
else{Write-Host "Data preserved at $DataRoot"}
