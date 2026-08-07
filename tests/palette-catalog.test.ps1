[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot "AISky.Desktop\Core\ColorPaletteCatalog.cs"
$source = Get-Content -LiteralPath $catalogPath -Raw
$definitionPattern = 'new\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*\[(.*?)\]\s*\)'
$definitions = [regex]::Matches(
    $source,
    $definitionPattern,
    [Text.RegularExpressions.RegexOptions]::Singleline)

if ($definitions.Count -lt 50) {
    throw "Expected at least 50 palettes, found $($definitions.Count)."
}

$ids = @($definitions | ForEach-Object { $_.Groups[1].Value })
$duplicates = @($ids | Group-Object | Where-Object Count -gt 1)
if ($duplicates.Count -gt 0) {
    throw "Duplicate palette id(s): $($duplicates.Name -join ', ')."
}

foreach ($definition in $definitions) {
    $id = $definition.Groups[1].Value
    $colors = [regex]::Matches($definition.Groups[3].Value, '"(#[0-9A-Fa-f]{6})"')
    if ($colors.Count -lt 2) {
        throw "Palette '$id' must contain at least two valid #RRGGBB colors."
    }
}

[ordered]@{
    status = "ok"
    palettes = $definitions.Count
    uniqueIds = $ids.Count
} | ConvertTo-Json
