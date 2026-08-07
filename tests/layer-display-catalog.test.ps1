$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot "AISky.Desktop\Core\LayerDisplayCatalog.cs"
$mainPagePath = Join-Path $repoRoot "AISky.Desktop\MainPage.xaml.cs"
$mapPath = Join-Path $repoRoot "AISky.Desktop\MapHost\map.js"

$catalog = Get-Content -LiteralPath $catalogPath -Raw
$mainPage = Get-Content -LiteralPath $mainPagePath -Raw
$map = Get-Content -LiteralPath $mapPath -Raw

$requiredDefinitions = @(
    '["t2m"] = new("°C", -10, 40)',
    '["qv2m"] = new("kg/kg", 0, 0.025, 0.001)',
    '["cldtot"] = new("无", 0, 1, 0.01)',
    '["slp"] = new("hPa", 950, 1050)',
    '["ducmass"] = new("g/m²", 0, 1, 0.001)',
    '["dusmass"] = new("μg/m³", 0, 500)',
    '["prectot"] = new("g/(m²·s)", 0, 1, 1d / 86.4d)',
    '["pblh"] = new("m", 0, 3000)'
)

foreach ($definition in $requiredDefinitions) {
    if (-not $catalog.Contains($definition, [StringComparison]::Ordinal)) {
        throw "Missing authoritative display definition: $definition"
    }
}

if (-not $mainPage.Contains(
    '.OrderBy(layer => layer.Code, StringComparer.OrdinalIgnoreCase)',
    [StringComparison]::Ordinal)) {
    throw "Default layer list is not alphabetically ordered."
}

foreach ($property in @("displayScale", "displayOffset")) {
    if (-not $mainPage.Contains($property, [StringComparison]::Ordinal)) {
        throw "Map payload is missing $property."
    }
}

if (-not $map.Contains(
    'return value * scale + offset;',
    [StringComparison]::Ordinal)) {
    throw "Map sampling does not apply the display conversion."
}

Write-Host "Layer display catalog tests passed." -ForegroundColor Green
