$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $repoRoot "AISky.Desktop\Core\AppSettings.cs"
$pageXamlPath = Join-Path $repoRoot "AISky.Desktop\MainPage.xaml"
$pageCodePath = Join-Path $repoRoot "AISky.Desktop\MainPage.xaml.cs"
$windowCodePath = Join-Path $repoRoot "AISky.Desktop\MainWindow.xaml.cs"
$dialogXamlPath = Join-Path $repoRoot "AISky.Desktop\TutorialDialog.xaml"
$dialogCodePath = Join-Path $repoRoot "AISky.Desktop\TutorialDialog.xaml.cs"

$settings = Get-Content -LiteralPath $settingsPath -Raw
$pageXaml = Get-Content -LiteralPath $pageXamlPath -Raw
$pageCode = Get-Content -LiteralPath $pageCodePath -Raw
$windowCode = Get-Content -LiteralPath $windowCodePath -Raw
$dialogXaml = Get-Content -LiteralPath $dialogXamlPath -Raw
$dialogCode = Get-Content -LiteralPath $dialogCodePath -Raw

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $settings 'ShowTutorialOnOpen\s*\{\s*get;\s*init;\s*\}\s*=\s*true' `
    "Tutorial must automatically open by default."
Assert-Contains $pageXaml 'AutomationProperties\.Name="打开新手教程"' `
    "The overflow menu must expose the tutorial entry."
Assert-Contains $pageCode 'ShowTutorialAsync\(automatic:\s*false\)' `
    "The manual tutorial command is not wired."
Assert-Contains $pageCode 'RequestAutomaticTutorial\(\)' `
    "The startup cover must queue the automatic tutorial."
Assert-Contains $pageCode 'ShowTutorialOnOpen\s*=\s*showOnOpen' `
    "The do-not-show choice must be persisted."
Assert-Contains $pageCode 'RequestedTheme\s*=\s*RootLayout\.ActualTheme' `
    "The tutorial must inherit the active light or dark theme."
Assert-Contains $windowCode 'PrepareTrayReveal\(\)' `
    "Tray hiding must prepare the reveal cover."
Assert-Contains $windowCode 'PlayTrayReveal\(\)' `
    "Tray restoration must play the reveal cover."
Assert-Contains $dialogXaml 'x:Name="DoNotShowAgainCheckBox"' `
    "The tutorial must contain a do-not-show checkbox."

$illustrations = @(
    "ForecastIllustration",
    "MapIllustration",
    "VariableIllustration",
    "WindIllustration",
    "SyncIllustration",
    "DisplayIllustration"
)
foreach ($illustration in $illustrations) {
    Assert-Contains $dialogXaml "x:Name=`"$illustration`"" `
        "Missing tutorial illustration: $illustration"
    Assert-Contains $dialogCode "\b$illustration\b" `
        "Tutorial illustration is not switched by the step controller: $illustration"
}

$stepCount = ([regex]::Matches($dialogCode, '(?m)^\s*new\(\s*$')).Count
if ($stepCount -ne 6) {
    throw "Expected 6 concise tutorial steps, found $stepCount."
}

Write-Host "Tutorial flow test passed." -ForegroundColor Green
