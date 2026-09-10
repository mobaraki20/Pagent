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
foreach($required in @($project,$transportProject,$serviceProject,$contractProject)){
  if(!(Test-Path $required -PathType Leaf)){throw "Acceptance project missing: $required"}
}
$sourceSha=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceSha)){throw 'Unable to resolve source SHA.'}

$manualUat=@('A48','A50','A51')
$transportCases=@('A04','A06','A17')
$serviceCases=@('A12','A13','A14','A15','A16')
$contractCases=@()
$implementedAutomated=@('A01','A02','A04','A05','A06','A07','A08','A09','A10','A12','A13','A14','A15','A16','A17','A24','A28','A44','A46')
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
  $caseDir=Join-Path $results $id
  New-Item $caseDir -ItemType Directory -Force | Out-Null
  $selectedProject=if($transportCases -contains $id){$transportProject}elseif($serviceCases -contains $id){$serviceProject}elseif($contractCases -contains $id){$contractProject}else{$project}
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
  if([string]::IsNullOrWhiteSpace($env:SOKNA_ACCEPTANCE_SERVER_URL) -or [string]::IsNullOrWhiteSpace($env:SOKNA_ACCEPTANCE_TOKEN_FILE)){
    $path=Join-Path $results 'A49.result.json'
    [ordered]@{
      case_id='A49';status='NOT_RUN';source_sha=$sourceSha;run_started_at=(Get-Date).ToUniversalTime().ToString('o');run_finished_at=(Get-Date).ToUniversalTime().ToString('o');exit_code=3;evidence=@();blocker='Set SOKNA_ACCEPTANCE_SERVER_URL and SOKNA_ACCEPTANCE_TOKEN_FILE for the real API integration gate.'
    } | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8NoBOM
    Write-Error 'Integration configuration is missing. A49 remains NOT_RUN.'
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
