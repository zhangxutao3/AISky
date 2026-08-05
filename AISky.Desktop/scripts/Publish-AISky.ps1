[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Repository = '',
    [string]$ReleaseTitle = '',
    [string]$NotesFile = '',
    [string]$OutputDirectory = '',
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..')).Path
$appProject = Join-Path $projectRoot 'AISky.Desktop.csproj'
$updaterProject = Join-Path $workspaceRoot 'AISky.Updater\AISky.Updater.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot "artifacts\v$Version"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$stageDirectory = Join-Path $OutputDirectory 'AISky-Desktop'
$updaterDirectory = Join-Path $OutputDirectory '_updater'
$archivePath = Join-Path $OutputDirectory 'AISky-Desktop-win-x64.zip'
$hashPath = "$archivePath.sha256"

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "输出目录已存在：$OutputDirectory`n为避免覆盖旧版本，请更换版本号或输出目录。"
}
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $updaterDirectory -Force | Out-Null

Write-Host "正在发布 AISky $Version..."
dotnet publish $updaterProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -o $updaterDirectory
if ($LASTEXITCODE -ne 0) {
    throw '更新助手发布失败。'
}

dotnet publish $appProject `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:AssemblyVersion="${Version}.0" `
    -p:FileVersion="${Version}.0" `
    -p:InformationalVersion=$Version `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -o $stageDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'AISky 主程序发布失败。'
}

# WinUI 3 的 unpackaged 发布输出在部分 SDK 组合下不会自动携带
# PRI/XBF 资源。它们由上面的 Release 构建生成，是应用启动所必需的。
$releaseBuildRoot = Join-Path $projectRoot 'bin\x64\Release'
$xamlResourceRoot = Get-ChildItem -LiteralPath $releaseBuildRoot -Recurse -File -Filter 'AISky.Desktop.pri' |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'AISky.Desktop.exe')) -and
        (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'MainWindow.xbf'))
    } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty DirectoryName
if ([string]::IsNullOrWhiteSpace($xamlResourceRoot)) {
    throw 'Release 构建输出中缺少 WinUI PRI/XBF 资源。'
}
Copy-Item -LiteralPath (Join-Path $xamlResourceRoot 'AISky.Desktop.pri') -Destination $stageDirectory
Get-ChildItem -LiteralPath $xamlResourceRoot -File -Filter '*.xbf' |
    Copy-Item -Destination $stageDirectory

$updaterExecutable = Join-Path $updaterDirectory 'AISky.Updater.exe'
if (-not (Test-Path -LiteralPath $updaterExecutable)) {
    throw '发布输出中缺少 AISky.Updater.exe。'
}
Copy-Item -LiteralPath $updaterExecutable -Destination $stageDirectory
Set-Content -LiteralPath (Join-Path $stageDirectory 'VERSION.txt') -Value $Version -Encoding UTF8

$requiredReleaseFiles = @(
    'AISky.Desktop.exe',
    'AISky.Desktop.pri',
    'App.xbf',
    'MainWindow.xbf',
    'MainPage.xbf',
    'SettingsDialog.xbf',
    'UpdateDialog.xbf',
    'AISky.Updater.exe',
    'Assets\AppIcon.ico',
    'MapHost\index.html'
)
foreach ($requiredFile in $requiredReleaseFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory $requiredFile))) {
        throw "发布输出缺少必需文件：$requiredFile"
    }
}

if (-not [string]::IsNullOrWhiteSpace($Repository)) {
    if ($Repository -notmatch '^(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)$') {
        throw 'Repository 必须使用 owner/repo 格式，例如 my-name/aisky-desktop。'
    }
    $configPath = Join-Path $stageDirectory 'Config\update-config.json'
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $config.repositoryOwner = $Matches.owner
    $config.repositoryName = $Matches.repo
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
}

Compress-Archive -Path (Join-Path $stageDirectory '*') -DestinationPath $archivePath
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "sha256:$hash  AISky-Desktop-win-x64.zip" -Encoding ASCII

Write-Host "发布包已生成：$archivePath"
Write-Host "SHA-256：$hash"

if (-not $Upload) {
    return
}
if ([string]::IsNullOrWhiteSpace($Repository)) {
    throw '使用 -Upload 时必须提供 -Repository owner/repo。'
}
if ([string]::IsNullOrWhiteSpace($NotesFile)) {
    throw '使用 -Upload 时必须提供 -NotesFile。'
}
$NotesFile = (Resolve-Path $NotesFile).Path
if ([string]::IsNullOrWhiteSpace($ReleaseTitle)) {
    $ReleaseTitle = "AISky $Version"
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI 尚未登录，请先运行 gh auth login。'
}
gh release create "v$Version" `
    $archivePath `
    $hashPath `
    --repo $Repository `
    --title $ReleaseTitle `
    --notes-file $NotesFile `
    --latest
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub Release 创建失败。'
}
Write-Host "GitHub Release v$Version 已发布。"
