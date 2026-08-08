$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$workerPath = Join-Path $repoRoot "AISky.Desktop\DataWorker\worker.py"
$assetRoot = Join-Path $repoRoot "AISky.Desktop\Assets\Layers"
$generatorPath = Join-Path $repoRoot "AISky.Desktop\scripts\Generate-VisualAssets.py"

$worker = Get-Content -LiteralPath $workerPath -Raw
$generator = Get-Content -LiteralPath $generatorPath -Raw
$matches = [regex]::Matches(
    $worker,
    'LayerSpec\(\s*"([^"]+)"',
    [Text.RegularExpressions.RegexOptions]::Singleline)
$layerIds = @($matches | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)

if ($layerIds.Count -lt 40) {
    throw "Unexpectedly found only $($layerIds.Count) layer definitions."
}

foreach ($layerId in $layerIds) {
    $path = Join-Path $assetRoot "$layerId.png"
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing fixed layer thumbnail: $layerId.png"
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 1024 `
        -or $bytes[0] -ne 0x89 `
        -or $bytes[1] -ne 0x50 `
        -or $bytes[2] -ne 0x4E `
        -or $bytes[3] -ne 0x47) {
        throw "Invalid or unexpectedly small PNG thumbnail: $layerId.png"
    }
}

if (-not $generator.Contains('ccrs.Orthographic(', [StringComparison]::Ordinal) `
    -or $generator.Contains('coastlines(', [StringComparison]::Ordinal) `
    -or $generator.Contains('BORDERS', [StringComparison]::Ordinal)) {
    throw "Layer thumbnails must use a border-free Cartopy Orthographic projection."
}

Write-Host "Layer thumbnail assets passed: $($layerIds.Count) fixed globe images." -ForegroundColor Green
