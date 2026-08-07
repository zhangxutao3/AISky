[CmdletBinding()]
param(
    [string]$Python = "python",
    [switch]$Quick
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

    $testFiles = if ($Quick) {
        @("smoke_test.py")
    }
    else {
        @(
            "smoke_test.py",
            "phase5_test.py",
            "phase7_resilience_test.py"
        )
    }
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

    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($null -ne $node) {
        Write-Host "Running map-math.test.js"
        & $node.Source (Join-Path $repoRoot "tests\map-math.test.js")
        if ($LASTEXITCODE -ne 0) {
            throw "map-math.test.js failed."
        }

        Write-Host "Running map-boundary-assets.test.js"
        & $node.Source (Join-Path $repoRoot "tests\map-boundary-assets.test.js")
        if ($LASTEXITCODE -ne 0) {
            throw "map-boundary-assets.test.js failed."
        }

        Write-Host "Running typhoon-algorithm.test.js"
        & $node.Source (Join-Path $repoRoot "tests\typhoon-algorithm.test.js")
        if ($LASTEXITCODE -ne 0) {
            throw "typhoon-algorithm.test.js failed."
        }
    }
    elseif (-not $Quick) {
        throw "Node.js is required for the typhoon path algorithm test."
    }

    Write-Host "Running palette-catalog.test.ps1"
    & (Join-Path $repoRoot "tests\palette-catalog.test.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "palette-catalog.test.ps1 failed."
    }

    Write-Host "Running layer-display-catalog.test.ps1"
    & (Join-Path $repoRoot "tests\layer-display-catalog.test.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "layer-display-catalog.test.ps1 failed."
    }

    Write-Host "Running tutorial-flow.test.ps1"
    & (Join-Path $repoRoot "tests\tutorial-flow.test.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "tutorial-flow.test.ps1 failed."
    }

    if ($Quick) {
        Write-Host "AISky quick data-pipeline test passed." -ForegroundColor Green
    }
    else {
        Write-Host "AISky full data-pipeline tests passed." -ForegroundColor Green
    }
}
finally {
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTarget.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $resolvedTarget)) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
