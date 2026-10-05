# scripts/run-e2e.ps1
# Runs the browser tests against a throwaway instance: Test environment, mock AI, no Key Vault.
# Usage: pwsh scripts/run-e2e.ps1 [-Filter <test name fragment>]
param([string]$Filter)

$ErrorActionPreference = 'Stop'
$url = 'http://localhost:4100'

dotnet build PoRedoMedia.slnx -v q
if ($LASTEXITCODE -ne 0) { exit 1 }

$env:ASPNETCORE_ENVIRONMENT = 'Test'
$env:Mocks__UseMockAi = 'true'
$app = Start-Process dotnet -PassThru -WindowStyle Hidden -ArgumentList @(
    'run', '--project', 'src/PoRedoMedia.Api', '--no-build', '--no-launch-profile', '--urls', $url)
try {
    $up = $false
    foreach ($i in 1..60) {
        try { Invoke-WebRequest "$url/health/live" -TimeoutSec 2 | Out-Null; $up = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { Write-Error "The app did not start on $url." }

    $env:E2E_BASE_URL = $url
    $testArgs = @('test', 'tests/PoRedoMedia.E2EUI', '--no-build')
    if ($Filter) { $testArgs += @('--filter', "FullyQualifiedName~$Filter") }
    dotnet @testArgs
    $code = $LASTEXITCODE
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Get-Process PoRedoMedia.Api -ErrorAction SilentlyContinue | Stop-Process -Force
}
exit $code
