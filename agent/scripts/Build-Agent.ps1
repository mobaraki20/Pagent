param(
  [string]$Configuration='Release',
  [string]$Runtime='win-x64',
  [string]$Output=(Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version='6.0.0'
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
Push-Location $root
try {
$sdk=& $dotnet --version
if(-not $sdk.StartsWith('10.')){throw ".NET 10 SDK required; found $sdk"}
$sourceCommit=(& git rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)){throw 'Unable to resolve source commit.'}

Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Output -ItemType Directory -Force|Out-Null

$solution=Join-Path $root 'Sokna.PrintAgent.slnx'
& $dotnet restore $solution
if($LASTEXITCODE -ne 0){throw 'dotnet restore failed.'}
Write-Host '== NuGet dependency graph ==' -ForegroundColor Cyan
& $dotnet list $solution package --include-transitive
if($LASTEXITCODE -ne 0){throw 'dotnet package graph failed.'}
Write-Host '== NuGet vulnerability audit ==' -ForegroundColor Cyan
& $dotnet list $solution package --vulnerable --include-transitive
if($LASTEXITCODE -ne 0){throw 'dotnet vulnerability audit command failed.'}

& $dotnet build $solution -c $Configuration --no-restore
if($LASTEXITCODE -ne 0){throw 'dotnet build failed.'}
& $dotnet run --project (Join-Path $root 'tests\Sokna.PrintAgent.Tests\Sokna.PrintAgent.Tests.csproj') -c $Configuration --no-build
if($LASTEXITCODE -ne 0){throw 'Agent tests failed.'}

foreach($project in @('Sokna.PrintAgent.Service','Sokna.PrintAgent.Worker','Sokna.PrintAgent.Control')){
  $proj=Join-Path $root "src\$project\$project.csproj"
  $dest=Join-Path $Output $project
  & $dotnet restore $proj -r $Runtime
  if($LASTEXITCODE -ne 0){throw "dotnet runtime restore failed: $project"}
  & $dotnet publish $proj -c $Configuration -r $Runtime --self-contained true --no-restore -o $dest
  if($LASTEXITCODE -ne 0){throw "dotnet publish failed: $project"}
}

$package=Join-Path $Output 'package'
$payload=Join-Path $package 'payload'
$docs=Join-Path $package 'docs'
New-Item $payload -ItemType Directory -Force|Out-Null
New-Item $docs -ItemType Directory -Force|Out-Null

# The installed uninstaller is part of the verified payload and survives Setup extraction.
Copy-Item (Join-Path $root 'installer\Uninstall-SoknaPrintAgent.ps1') (Join-Path $payload 'Uninstall-SoknaPrintAgent.ps1') -Force

# Each self-contained process owns its runtime dependency set. Do not flatten these outputs.
$layout=[ordered]@{
  'Sokna.PrintAgent.Service'='Service'
  'Sokna.PrintAgent.Worker'='Worker'
  'Sokna.PrintAgent.Control'='Control'
}
if(($layout.Values | Select-Object -Unique).Count -ne $layout.Count){throw 'Packaging layout contains duplicate component directories.'}
foreach($project in $layout.Keys){
  $src=Join-Path $Output $project
  $dest=Join-Path $payload $layout[$project]
  New-Item $dest -ItemType Directory -Force|Out-Null
  Copy-Item (Join-Path $src '*') $dest -Recurse -Force
}

# Collision guard: component binaries may share names and DIFFER by design, but they must never be flattened.
$flatBinaries=Get-ChildItem $payload -File | Where-Object {$_.Extension -in @('.dll','.exe')}
if($flatBinaries){throw "Packaging collision guard: component binary found at payload root: $($flatBinaries.Name -join ', ')"}
$componentFiles=foreach($component in $layout.Values){
  $dir=Join-Path $payload $component
  Get-ChildItem $dir -File -Recurse | ForEach-Object {[pscustomobject]@{Component=$component;Name=$_.Name;Path=$_.FullName}}
}
$isolatedCollisions=@()
$componentFiles | Group-Object Name | Where-Object {$_.Count -gt 1} | ForEach-Object {
  $hashes=$_.Group | ForEach-Object {(Get-FileHash $_.Path -Algorithm SHA256).Hash} | Select-Object -Unique
  if($hashes.Count -gt 1){$isolatedCollisions += $_.Name}
}
Write-Host "Packaging collision guard PASS; isolated differing-name collisions preserved: $($isolatedCollisions.Count)" -ForegroundColor Green
if($isolatedCollisions.Count -gt 0){Write-Host ($isolatedCollisions | Sort-Object | Select-Object -First 20 | ForEach-Object {"  isolated: $_"})}

Copy-Item (Join-Path $root 'installer\Install-SoknaPrintAgent.ps1') $package
Copy-Item (Join-Path $root 'installer\Uninstall-SoknaPrintAgent.ps1') $package
Copy-Item (Join-Path $root 'README_FA.md') $package
Copy-Item (Join-Path $root 'docs\*.md') $docs
Set-Content (Join-Path $package 'VERSION.txt') $version -Encoding utf8NoBOM

$manifest=@()
Get-ChildItem $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
  $relative=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/')
  $manifest += [ordered]@{path=$relative;size=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'PAYLOAD_MANIFEST.json') -Encoding utf8NoBOM

$buildInfo=[ordered]@{
  agent_version=$version;protocol_version=4;runtime=$Runtime;configuration=$Configuration
  dotnet_sdk=$sdk;built_at_utc=(Get-Date).ToUniversalTime().ToString('o')
  target_framework='net10.0-windows10.0.19041.0';self_contained=$true
  source_commit=$sourceCommit;source_tree='agent/'
}
$buildInfo | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $package 'BUILD_INFO.json') -Encoding utf8NoBOM

$zip=Join-Path $Output "Sokna-Print-Agent-$version-$Runtime.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal

# Build a single-file Windows bootstrapper with the verified package embedded.
$setupOut=Join-Path $Output 'Setup'
New-Item $setupOut -ItemType Directory -Force|Out-Null
$setupProject=Join-Path $root 'src\Sokna.PrintAgent.Setup\Sokna.PrintAgent.Setup.csproj'
& $dotnet restore $setupProject -r $Runtime
if($LASTEXITCODE -ne 0){throw 'dotnet runtime restore failed: Sokna.PrintAgent.Setup'}
& $dotnet publish $setupProject -c $Configuration -r $Runtime --self-contained true --no-restore `
  "-p:PayloadZip=$zip" -p:PublishSingleFile=true -o $setupOut
if($LASTEXITCODE -ne 0){throw 'dotnet publish failed: Sokna.PrintAgent.Setup'}
$setupExe=Join-Path $setupOut "Sokna-Print-Agent-$version-Setup.exe"
if(-not (Test-Path $setupExe -PathType Leaf)){throw "Setup executable was not produced: $setupExe"}
$setupFinal=Join-Path $Output ([IO.Path]::GetFileName($setupExe))
Copy-Item $setupExe $setupFinal -Force

$artifactRows=@()
foreach($artifact in @($zip,$setupFinal)){
  $artifactRows += [ordered]@{
    file=[IO.Path]::GetFileName($artifact)
    size=(Get-Item $artifact).Length
    sha256=(Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
  }
}
$artifactRows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Output "BUILD_ARTIFACTS-Agent-$version.json") -Encoding utf8NoBOM
$artifactRows | ForEach-Object { "$($_.sha256)  $($_.file)" } | Set-Content (Join-Path $Output "SHA256SUMS-Agent-$version.txt") -Encoding ascii
$artifactRows | ForEach-Object { Write-Host "Built: $($_.file) | $($_.size) bytes | SHA256=$($_.sha256)" -ForegroundColor Green }
}
finally { Pop-Location }
