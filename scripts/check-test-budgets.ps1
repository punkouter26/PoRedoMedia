# scripts/check-test-budgets.ps1
# Enforces the test suite quotas: Unit <= 100, Integration <= 50, E2E API <= 25, E2E UI <= 25.
#
# Counts test *cases* the way the runner does (every [InlineData] row is one), by asking the
# runner: `dotnet test --list-tests`. It used to grep for "[Fact" / "[Theory", which ignored
# theory rows entirely — the unit suite read 93 while running 116 — and missed stacked or custom
# attributes. Needs a Release build first (CI builds just before this step).

$ErrorActionPreference = 'Stop'

$Budgets = [ordered]@{
    'PoRedoMedia.UnitTests'        = 100
    'PoRedoMedia.IntegrationTests' = 50
    'PoRedoMedia.E2EAPI'           = 25
    'PoRedoMedia.E2EUI'            = 25
}

$violations = 0
foreach ($name in $Budgets.Keys) {
    $project = Join-Path 'tests' $name "$name.csproj"
    if (-not (Test-Path $project)) {
        Write-Host "::error::Test project not found: $project"
        $violations++
        continue
    }

    $output = dotnet test $project --configuration Release --no-build --list-tests 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        Write-Host "::error::Could not list the tests in $name (is it built in Release?)"
        $violations++
        continue
    }

    # The listing prints one indented line per case after this header.
    $listed = $false
    $count = 0
    foreach ($line in $output) {
        if ($line -match 'The following Tests are available') { $listed = $true; continue }
        if ($listed -and $line -match '^\s{4}\S') { $count++ }
    }

    $limit = $Budgets[$name]
    $ok = $count -le $limit
    Write-Host ('{0,-30} {1,3} / {2,3}  {3}' -f $name, $count, $limit, $(if ($ok) { 'OK' } else { 'OVER BUDGET' }))
    if (-not $ok) { $violations++ }
}

if ($violations -gt 0) {
    Write-Host "::error::Test budget check failed ($violations problem(s)). Prune redundant tests."
    exit 1
}
