[CmdletBinding()]
param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $repoRoot "AISky.Desktop\DataWorker\worker.py"
$schema = Join-Path $repoRoot "AISky.Desktop\Infrastructure\Database\schema.sql"
$tests = Join-Path $repoRoot "AISky.Desktop\DataWorker\tests"
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = [IO.Path]::GetFullPath(
    (Join-Path $temporaryBase ("aisky-stage7-tests-" + [Guid]::NewGuid().ToString("N")))
)

if (-not $temporaryRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create test data outside the system temporary directory."
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $fixture = Join-Path $temporaryRoot "AISky-SDS_20260605_1930+20260605_1930_V01.nc"
    & $Python (Join-Path $tests "create_fixture.py") --output $fixture
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create the NetCDF test fixture."
    }

    $testFiles = @(
        "smoke_test.py",
        "phase5_test.py",
        "phase7_resilience_test.py"
    )
    foreach ($testFile in $testFiles) {
        Write-Host "Running $testFile"
        & $Python (Join-Path $tests $testFile) `
            --source $fixture `
            --worker $worker `
            --schema $schema
        if ($LASTEXITCODE -ne 0) {
            throw "$testFile failed."
        }
    }

    Write-Host "AISky data-pipeline tests passed." -ForegroundColor Green
}
finally {
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTarget.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $resolvedTarget)) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
