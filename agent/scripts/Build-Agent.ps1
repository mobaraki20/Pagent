param(
  [string]$Configuration='Release',
  [string]$Runtime='win-x64',
  [string]$Output=(Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$buildProps=Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version=[string]$buildProps.Project.PropertyGroup.SoknaAgentVersion
if([string]::IsNullOrWhiteSpace($version)){throw 'SoknaAgentVersion is missing from Directory.Build.props.'}
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
& $dotnet run --project (Join-Path $root 'tests\Sokna.PrintAgent.Worker.Tests\Sokna.PrintAgent.Worker.Tests.csproj') -c $Configuration --no-build
if($LASTEXITCODE -ne 0){throw 'Worker raster tests failed.'}

# Run only acceptance cases implemented at this remediation stage. The public
# Test-Agent-Acceptance.ps1 -Suite Automated intentionally remains strict and will fail
# until every automated A-case is implemented; CI must not turn missing cases into PASS.
$acceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.Acceptance\Sokna.PrintAgent.Acceptance.csproj'
$transportAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.TransportAcceptance\Sokna.PrintAgent.TransportAcceptance.csproj'
$serviceAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.ServiceAcceptance\Sokna.PrintAgent.ServiceAcceptance.csproj'
$contractAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.ContractAcceptance\Sokna.PrintAgent.ContractAcceptance.csproj'
$windowsFaultAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.WindowsFaultAcceptance\Sokna.PrintAgent.WindowsFaultAcceptance.csproj'
$cleanupAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.CleanupAcceptance\Sokna.PrintAgent.CleanupAcceptance.csproj'
$bridgeRuntimeAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeRuntimeAcceptance\Sokna.PrintAgent.BridgeRuntimeAcceptance.csproj'
$bridgeAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeAcceptance\Sokna.PrintAgent.BridgeAcceptance.csproj'
$bridgeSecurityAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeSecurityAcceptance\Sokna.PrintAgent.BridgeSecurityAcceptance.csproj'
$bridgeLoadAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeLoadAcceptance\Sokna.PrintAgent.BridgeLoadAcceptance.csproj'
$previewAcceptanceProject=Join-Path $root 'tests\Sokna.PrintAgent.PreviewAcceptance\Sokna.PrintAgent.PreviewAcceptance.csproj'
$transportAcceptanceCases=@('A04','A06','A17')
$serviceAcceptanceCases=@('A12','A13','A14','A15','A16','A19','A20','A21','A22','A23','A30')
$contractAcceptanceCases=@('A18')
$windowsFaultAcceptanceCases=@('A25','A26','A27')
$cleanupAcceptanceCases=@('A29')
$bridgeRuntimeAcceptanceCases=@('A32')
$bridgeAcceptanceCases=@('A33')
$bridgeSecurityAcceptanceCases=@('A34')
$previewAcceptanceCases=@('A35','A37','A38','A47')
$bridgeLoadAcceptanceCases=@('A36')
$acceptanceResults=Join-Path $Output 'acceptance-smoke'
New-Item $acceptanceResults -ItemType Directory -Force|Out-Null
$implementedAcceptance=@('A01','A02','A04','A05','A06','A07','A08','A09','A10','A12','A13','A14','A15','A16','A17','A18','A19','A20','A21','A22','A23','A24','A25','A26','A27','A28','A29','A30','A32','A33','A34','A35','A36','A37','A38','A44','A46','A47')
foreach($caseId in $implementedAcceptance){
  $caseDir=Join-Path $acceptanceResults $caseId
  New-Item $caseDir -ItemType Directory -Force|Out-Null
  $caseProject=if($transportAcceptanceCases -contains $caseId){$transportAcceptanceProject}elseif($serviceAcceptanceCases -contains $caseId){$serviceAcceptanceProject}elseif($contractAcceptanceCases -contains $caseId){$contractAcceptanceProject}elseif($windowsFaultAcceptanceCases -contains $caseId){$windowsFaultAcceptanceProject}elseif($cleanupAcceptanceCases -contains $caseId){$cleanupAcceptanceProject}elseif($bridgeRuntimeAcceptanceCases -contains $caseId){$bridgeRuntimeAcceptanceProject}elseif($bridgeSecurityAcceptanceCases -contains $caseId){$bridgeSecurityAcceptanceProject}elseif($previewAcceptanceCases -contains $caseId){$previewAcceptanceProject}elseif($bridgeLoadAcceptanceCases -contains $caseId){$bridgeLoadAcceptanceProject}elseif($bridgeAcceptanceCases -contains $caseId){$bridgeAcceptanceProject}else{$acceptanceProject}
  Write-Host "== Acceptance $caseId ==" -ForegroundColor Cyan
  & $dotnet run --project $caseProject -c $Configuration --no-build -- --case $caseId --results $caseDir
  if($LASTEXITCODE -ne 0){throw "Acceptance case failed: $caseId"}
}

# Service/Worker/Control are intentionally framework-dependent. One machine-wide .NET 10
# Desktop Runtime x64 services all three components and avoids embedding duplicate runtime copies.
foreach($project in @('Sokna.PrintAgent.Service','Sokna.PrintAgent.Worker','Sokna.PrintAgent.Control')){
  $proj=Join-Path $root "src\$project\$project.csproj"
  $dest=Join-Path $Output $project
  & $dotnet restore $proj -r $Runtime
  if($LASTEXITCODE -ne 0){throw "dotnet runtime restore failed: $project"}
  & $dotnet publish $proj -c $Configuration -r $Runtime --self-contained false --no-restore -o $dest
  if($LASTEXITCODE -ne 0){throw "dotnet publish failed: $project"}
  $runtimeConfig=Join-Path $dest "$project.runtimeconfig.json"
  if(-not (Test-Path $runtimeConfig -PathType Leaf)){throw "Framework-dependent runtimeconfig missing: $project"}
  if(Test-Path (Join-Path $dest 'coreclr.dll') -PathType Leaf){throw "Framework-dependent publish unexpectedly contains coreclr.dll: $project"}
}

$package=Join-Path $Output 'package'
$payload=Join-Path $package 'payload'
$docs=Join-Path $package 'docs'
New-Item $payload -ItemType Directory -Force|Out-Null
New-Item $docs -ItemType Directory -Force|Out-Null

Copy-Item (Join-Path $root 'installer\Uninstall-SoknaPrintAgent.ps1') (Join-Path $payload 'Uninstall-SoknaPrintAgent.ps1') -Force

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
  agent_version=$version
  protocol_version=4
  runtime=$Runtime
  configuration=$Configuration
  dotnet_sdk=$sdk
  built_at_utc=(Get-Date).ToUniversalTime().ToString('o')
  target_framework='net10.0-windows10.0.19041.0'
  component_deployment='framework-dependent'
  required_runtime='Microsoft.WindowsDesktop.App 10.x x64 (includes Microsoft.NETCore.App)'
  runtime_bootstrap_url='https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
  setup_self_contained=$true
  setup_single_file_compression=$true
  source_commit=$sourceCommit
  source_tree='agent/'
}
$buildInfo | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $package 'BUILD_INFO.json') -Encoding utf8NoBOM

$zip=Join-Path $Output "Sokna-Print-Agent-$version-$Runtime.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal

$setupProject=Join-Path $root 'src\Sokna.PrintAgent.Setup\Sokna.PrintAgent.Setup.csproj'
& $dotnet restore $setupProject -r $Runtime
if($LASTEXITCODE -ne 0){throw 'dotnet runtime restore failed: Sokna.PrintAgent.Setup'}

# Benchmark the Hybrid payload with an uncompressed self-contained Setup host first.
$setupBenchmarkOut=Join-Path $Output 'SetupBenchmarkUncompressed'
New-Item $setupBenchmarkOut -ItemType Directory -Force|Out-Null
& $dotnet publish $setupProject -c $Configuration -r $Runtime --self-contained true --no-restore `
  "-p:PayloadZip=$zip" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o $setupBenchmarkOut
if($LASTEXITCODE -ne 0){throw 'dotnet publish failed: Sokna.PrintAgent.Setup uncompressed benchmark'}
$setupBenchmarkExe=Join-Path $setupBenchmarkOut "Sokna-Print-Agent-$version-Setup.exe"
if(-not (Test-Path $setupBenchmarkExe -PathType Leaf)){throw 'Uncompressed Setup benchmark executable was not produced.'}
$setupBenchmarkBytes=(Get-Item $setupBenchmarkExe).Length

# Final Setup remains self-contained so it can bootstrap .NET on a machine that has no runtime.
$setupOut=Join-Path $Output 'Setup'
New-Item $setupOut -ItemType Directory -Force|Out-Null
& $dotnet publish $setupProject -c $Configuration -r $Runtime --self-contained true --no-restore `
  "-p:PayloadZip=$zip" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $setupOut
if($LASTEXITCODE -ne 0){throw 'dotnet publish failed: Sokna.PrintAgent.Setup'}
$setupExe=Join-Path $setupOut "Sokna-Print-Agent-$version-Setup.exe"
if(-not (Test-Path $setupExe -PathType Leaf)){throw "Setup executable was not produced: $setupExe"}
$setupFinal=Join-Path $Output ([IO.Path]::GetFileName($setupExe))
Copy-Item $setupExe $setupFinal -Force

$zipBytes=(Get-Item $zip).Length
$setupBytes=(Get-Item $setupFinal).Length
$baselineSetupBytes=331159774L
$baselineZipBytes=189423098L
$reductionSetup=[Math]::Round((1-($setupBytes/[double]$baselineSetupBytes))*100,2)
$reductionZip=[Math]::Round((1-($zipBytes/[double]$baselineZipBytes))*100,2)
$compressionGain=[Math]::Round((1-($setupBytes/[double]$setupBenchmarkBytes))*100,2)
$sizeEvidence=[ordered]@{
  baseline=[ordered]@{
    description='Fully self-contained Service + Worker + Control + self-contained Setup'
    source_commit='e71a03c8629f00dcb55657539d93cfabdfffce48'
    workflow_run=34544366455
    setup_bytes=$baselineSetupBytes
    zip_bytes=$baselineZipBytes
  }
  hybrid_uncompressed_setup=[ordered]@{
    setup_bytes=$setupBenchmarkBytes
    component_deployment='framework-dependent'
    setup_self_contained=$true
    setup_single_file_compression=$false
  }
  hybrid_final=[ordered]@{
    setup_bytes=$setupBytes
    zip_bytes=$zipBytes
    component_deployment='framework-dependent'
    setup_self_contained=$true
    setup_single_file_compression=$true
    setup_reduction_percent_vs_baseline=$reductionSetup
    zip_reduction_percent_vs_baseline=$reductionZip
    setup_compression_gain_percent_vs_hybrid_uncompressed=$compressionGain
  }
}
$sizeEvidence | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Output "PACKAGE_SIZE_EVIDENCE-Agent-$version.json") -Encoding utf8NoBOM
Write-Host "Size benchmark: baseline_setup=$baselineSetupBytes hybrid_uncompressed_setup=$setupBenchmarkBytes final_setup=$setupBytes reduction=$reductionSetup%" -ForegroundColor Cyan
Write-Host "Size benchmark: baseline_zip=$baselineZipBytes final_zip=$zipBytes reduction=$reductionZip%" -ForegroundColor Cyan

# Benchmark-only executable is evidence-by-number; do not inflate the distributable CI artifact with it.
Remove-Item $setupBenchmarkOut -Recurse -Force -ErrorAction SilentlyContinue

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
