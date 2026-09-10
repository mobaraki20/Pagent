param([string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
  dotnet restore .\Sokna.PrintAgent.slnx
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
  dotnet build .\Sokna.PrintAgent.slnx -c $Configuration --no-restore
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
  dotnet run --project .\tests\Sokna.PrintAgent.Remediation.Tests\Sokna.PrintAgent.Remediation.Tests.csproj -c $Configuration --no-build
  exit $LASTEXITCODE
}
finally { Pop-Location }
