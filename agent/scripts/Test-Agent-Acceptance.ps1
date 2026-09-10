[CmdletBinding(DefaultParameterSetName='Case')]
param(
  [Parameter(Mandatory=$true,ParameterSetName='Case')]
  [ValidatePattern('^A(0[1-9]|[1-4][0-9]|5[0-2])$')]
  [string]$CaseId,

  [Parameter(Mandatory=$true,ParameterSetName='Suite')]
  [ValidateSet('Automated','Integration')]
  [string]$Suite,

  [Parameter(Mandatory=$true)]
  [string]$ResultsDirectory,

  [string]$Configuration='Release'
)

$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results=[IO.Path]::GetFullPath($ResultsDirectory)
New-Item $results -ItemType Directory -Force | Out-Null
$project=Join-Path $root 'tests\Sokna.PrintAgent.Acceptance\Sokna.PrintAgent.Acceptance.csproj'
$transportProject=Join-Path $root 'tests\Sokna.PrintAgent.TransportAcceptance\Sokna.PrintAgent.TransportAcceptance.csproj'
$serviceProject=Join-Path $root 'tests\Sokna.PrintAgent.ServiceAcceptance\Sokna.PrintAgent.ServiceAcceptance.csproj'
$contractProject=Join-Path $root 'tests\Sokna.PrintAgent.ContractAcceptance\Sokna.PrintAgent.ContractAcceptance.csproj'
$windowsFaultProject=Join-Path $root 'tests\Sokna.PrintAgent.WindowsFaultAcceptance\Sokna.PrintAgent.WindowsFaultAcceptance.csproj'
$cleanupProject=Join-Path $root 'tests\Sokna.PrintAgent.CleanupAcceptance\Sokna.PrintAgent.CleanupAcceptance.csproj'
$bridgeProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeAcceptance\Sokna.PrintAgent.BridgeAcceptance.csproj'
$bridgeSecurityProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeSecurityAcceptance\Sokna.PrintAgent.BridgeSecurityAcceptance.csproj'
$bridgeLoadProject=Join-Path $root 'tests\Sokna.PrintAgent.BridgeLoadAcceptance\Sokna.PrintAgent.BridgeLoadAcceptance.csproj'
$realApiProject=Join-Path $root 'tests\Sokna.PrintAgent.RealApiIntegration\Sokna.PrintAgent.RealApiIntegration.csproj'
foreach($required in @($project,$transportProject,$serviceProject,$contractProject,$windowsFaultProject,$cleanupProject,$bridgeProject,$bridgeSecurityProject,$bridgeLoadProject,$realApiProject)){
  if(!(Test-Path $required -PathType Leaf)){throw "Acceptance project missing: $required"}
}
$sourceSha=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceSha)){throw 'Unable to resolve source SHA.'}

$manualUat=@('A48','A50','A51')
$transportCases=@('A04','A06','A17')
$serviceCases=@('A12','A13','A14','A15','A16')
$contractCases=@('A18')
$windowsFaultCases=@('A25','A26','A27')
$cleanupCases=@('A29')
$bridgeCases=@('A32','A33')
$bridgeSecurityCases=@('A34')
$bridgeLoadCases=@('A36')
$integrationCases=@('A49')
# A32 has a partial harness but is intentionally NOT in implementedAutomated until heartbeat
# consumes BridgeRuntimeState rather than configuration intent.
$implementedAutomated=@('A01','A02','A04','A05','A06','A07','A08','A09','A10','A12','A13','A14','A15','A16','A17','A18','A24','A25','A26','A27','A28','A29','A33','A34','A36','A44','A46')
$allAutomated=1..47 | ForEach-Object {'A{0:D2}' -f $_}
$allAutomated+=@('A52')

function Write-ManualUatResult([string]$id){
  $path=Join-Path $results "$id.result.json"
  [ordered]@{
    case_id=$id
    status='UAT_REQUIRED'
    source_sha=$sourceSha
    run_started_at=(Get-Date).ToUniversalTime().ToString('o')
    run_finished_at=(Get-Date).ToUniversalTime().ToString('o')
    exit_code=4
    evidence=@()
    blocker='Requires target Windows/browser/physical printer evidence; automated PASS is forbidden.'
  } | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8NoBOM
  Write-Warning "$id => UAT_REQUIRED"
  return 4
}

function Invoke-Case([string]$id){
  if($manualUat -contains $id){return (Write-ManualUatResult $id)}
  if($id -eq 'A32' -and -not ($implementedAutomated -contains 'A32')){
    $path=Join-Path $results 'A32.result.json'
    [ordered]@{case_id='A32';status='NOT_RUN';source_sha=$sourceSha;run_started_at=(Get-Date).ToUniversalTime().ToString('o');run_finished_at=(Get-Date).ToUniversalTime().ToString('o');exit_code=3;evidence=@();blocker='Bridge reload harness exists, but A32 remains incomplete until heartbeat reports the actual active BridgeRuntimeState generation.'} | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8NoBOM
    return 3
  }
  $caseDir=Join-Path $results $id
  New-Item $caseDir -ItemType Directory -Force | Out-Null
  $selectedProject=if($transportCases -contains $id){$transportProject}elseif($serviceCases -contains $id){$serviceProject}elseif($contractCases -contains $id){$contractProject}elseif($windowsFaultCases -contains $id){$windowsFaultProject}elseif($cleanupCases -contains $id){$cleanupProject}elseif($bridgeSecurityCases -contains $id){$bridgeSecurityProject}elseif($bridgeLoadCases -contains $id){$bridgeLoadProject}elseif($bridgeCases -contains $id){$bridgeProject}elseif($integrationCases -contains $id){$realApiProject}else{$project}
  & dotnet run --project $selectedProject -c $Configuration -- --case $id --results $caseDir
  return $LASTEXITCODE
}

if($PSCmdlet.ParameterSetName -eq 'Case'){
  $code=Invoke-Case $CaseId
  if($code -ne 0){exit $code}
  exit 0
}

$caseIds=if($Suite -eq 'Automated'){$allAutomated}else{@('A49')}
if($Suite -eq 'Integration'){
  $missing=@()
  if([string]::IsNullOrWhiteSpace($env:SOKNA_ACCEPTANCE_SERVER_URL)){$missing+='SOKNA_ACCEPTANCE_SERVER_URL'}
  if([string]::IsNullOrWhiteSpace($env:SOKNA_ACCEPTANCE_FAULT_PROXY_URL)){$missing+='SOKNA_ACCEPTANCE_FAULT_PROXY_URL'}
  if([string]::IsNullOrWhiteSpace($env:SOKNA_ACCEPTANCE_TOKEN_FILE)){$missing+='SOKNA_ACCEPTANCE_TOKEN_FILE'}
  if([string]::IsNullOrWhiteSpace($env:SOKNA_ACCEPTANCE_DESTINATION_KEY)){$missing+='SOKNA_ACCEPTANCE_DESTINATION_KEY'}
  if($env:SOKNA_ACCEPTANCE_ALLOW_MUTATION -ne 'I_UNDERSTAND_THIS_MUTATES_ACCEPTANCE_API'){$missing+='SOKNA_ACCEPTANCE_ALLOW_MUTATION'}
  if($missing.Count -gt 0){
    $path=Join-Path $results 'A49.result.json'
    $blocker='A49 real integration is guarded and remains NOT_RUN. Missing/invalid: '+($missing -join ', ')+'. The fault proxy must forward the request, wait for upstream commit/response, then drop the client response when X-Sokna-Acceptance-Drop-Response=after-commit is present.'
    [ordered]@{
      case_id='A49';status='NOT_RUN';source_sha=$sourceSha;run_started_at=(Get-Date).ToUniversalTime().ToString('o');run_finished_at=(Get-Date).ToUniversalTime().ToString('o');exit_code=3;evidence=@();blocker=$blocker
    } | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8NoBOM
    Write-Error $blocker
    exit 3
  }
}

$failed=@()
foreach($id in $caseIds){
  Write-Host "== Acceptance $id ==" -ForegroundColor Cyan
  $code=Invoke-Case $id
  if($code -ne 0){$failed+="$id(exit=$code)"}
}
if($failed.Count -gt 0){
  Write-Error ("Acceptance suite failed/not-run: "+($failed -join ', '))
  exit 1
}
Write-Host "Acceptance suite $Suite PASS ($($caseIds.Count) cases)" -ForegroundColor Green
exit 0
