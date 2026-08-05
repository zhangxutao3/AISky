[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Repository = '',
    [string]$ReleaseTitle = '',
    [string]$NotesFile = '',
    [string]$OutputDirectory = '',
    [string]$PythonArchivePath = '',
    [string]$PythonRuntimePath = '',
    [string]$InstallerCompilerPath = '',
    [string]$SigningCertificateThumbprint = '',
    [string]$SignToolPath = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipInstaller,
    [switch]$AllowUnsignedRelease,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'

function Resolve-CodeSigningTool {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "SignTool 不存在：$resolved"
        }
        return $resolved
    }

    $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    $candidate = Get-ChildItem -LiteralPath $packageRoot -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw '没有找到 Microsoft SignTool.exe。请安装 Windows SDK 或使用 -SignToolPath 指定。'
    }
    return $candidate
}

function Assert-CodeSigningCertificate {
    param([string]$Thumbprint)

    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw 'SigningCertificateThumbprint 必须是 40 位十六进制证书指纹。'
    }
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $normalized } |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw "证书存储中没有找到指纹为 $normalized 的证书。"
    }
    if (-not $certificate.HasPrivateKey) {
        throw '指定证书没有可用的私钥，无法签名。'
    }
    if ($certificate.EnhancedKeyUsageList.ObjectId.Value -notcontains '1.3.6.1.5.5.7.3.3') {
        throw '指定证书不包含 Code Signing 扩展密钥用途。'
    }
    $now = Get-Date
    if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) {
        throw "指定证书当前不在有效期内（$($certificate.NotBefore) - $($certificate.NotAfter)）。"
    }
    return $normalized
}

function Invoke-AuthenticodeSign {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ToolPath,
        [Parameter(Mandatory = $true)]
        [string]$Thumbprint,
        [Parameter(Mandatory = $true)]
        [string]$TimestampServer
    )

    & $ToolPath sign `
        /sha1 $Thumbprint `
        /fd SHA256 `
        /td SHA256 `
        /tr $TimestampServer `
        /d 'AISky 桌面气象平台' `
        $Path
    if ($LASTEXITCODE -ne 0) {
        throw "代码签名失败：$Path"
    }
    & $ToolPath verify /pa /all /tw $Path
    if ($LASTEXITCODE -ne 0) {
        throw "代码签名验证失败：$Path"
    }
}

function Assert-AuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ToolPath
    )

    & $ToolPath verify /pa /all /tw $Path
    if ($LASTEXITCODE -ne 0) {
        throw "代码签名验证失败：$Path"
    }
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..')).Path
$appProject = Join-Path $projectRoot 'AISky.Desktop.csproj'
$updaterProject = Join-Path $workspaceRoot 'AISky.Updater\AISky.Updater.csproj'
$pythonVersion = '3.11.9'
$pythonArchiveName = "python-$pythonVersion-embed-amd64.zip"
$pythonDownloadUrl = "https://www.python.org/ftp/python/$pythonVersion/$pythonArchiveName"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot "artifacts\v$Version"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$stageDirectory = Join-Path $OutputDirectory 'AISky-Desktop'
$updaterDirectory = Join-Path $OutputDirectory '_updater'
$archivePath = Join-Path $OutputDirectory 'AISky-Desktop-win-x64.zip'
$hashPath = "$archivePath.sha256"
$installerScript = Join-Path $workspaceRoot 'installer\AISky.iss'
$installerPath = Join-Path $OutputDirectory 'AISky-Setup-win-x64.exe'
$installerHashPath = "$installerPath.sha256"
$signingEnabled = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
$normalizedSigningThumbprint = ''
if ($Upload -and -not $signingEnabled -and -not $AllowUnsignedRelease) {
    throw @'
正式上传默认要求可信 Authenticode 签名。
请提供 -SigningCertificateThumbprint；若明确接受 SmartScreen 的未知发布者提示，
可显式使用 -AllowUnsignedRelease。
'@
}
if ($signingEnabled) {
    $normalizedSigningThumbprint = Assert-CodeSigningCertificate $SigningCertificateThumbprint
    $SignToolPath = Resolve-CodeSigningTool $SignToolPath
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) `
        -or $timestampUri.Scheme -notin @('http', 'https')) {
        throw 'TimestampUrl 必须是有效的 HTTP(S) 绝对 URL。'
    }
}

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

Write-Host "正在准备内置 Python $pythonVersion 数据运行时..."
$pythonArchive = Join-Path $OutputDirectory $pythonArchiveName
$pythonRuntimeDirectory = Join-Path $stageDirectory 'Python'
$pythonSitePackages = Join-Path $pythonRuntimeDirectory 'Lib\site-packages'
if (-not [string]::IsNullOrWhiteSpace($PythonRuntimePath)) {
    if (-not [string]::IsNullOrWhiteSpace($PythonArchivePath)) {
        throw 'PythonRuntimePath 与 PythonArchivePath 不能同时使用。'
    }
    $PythonRuntimePath = (Resolve-Path -LiteralPath $PythonRuntimePath).Path
    if (-not (Test-Path -LiteralPath (Join-Path $PythonRuntimePath 'python.exe'))) {
        throw "指定的 Python 运行时缺少 python.exe：$PythonRuntimePath"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PythonRuntimePath 'Lib\site-packages\netCDF4\__init__.py'))) {
        throw "指定的 Python 运行时缺少 netCDF4：$PythonRuntimePath"
    }
    Write-Host "正在复用已验证的完整 Python 运行时：$PythonRuntimePath"
    New-Item -ItemType Directory -Path $pythonRuntimeDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $PythonRuntimePath -Force |
        Copy-Item -Destination $pythonRuntimeDirectory -Recurse
}
else {
    if ([string]::IsNullOrWhiteSpace($PythonArchivePath)) {
        Invoke-WebRequest -Uri $pythonDownloadUrl -OutFile $pythonArchive
    }
    else {
        $PythonArchivePath = (Resolve-Path -LiteralPath $PythonArchivePath).Path
        Write-Host "正在复用本地 Python 嵌入包：$PythonArchivePath"
        Copy-Item -LiteralPath $PythonArchivePath -Destination $pythonArchive
    }
    Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonRuntimeDirectory
    New-Item -ItemType Directory -Path $pythonSitePackages -Force | Out-Null

    $pythonPathFile = Join-Path $pythonRuntimeDirectory 'python311._pth'
    if (-not (Test-Path -LiteralPath $pythonPathFile)) {
        throw '内置 Python 缺少 python311._pth。'
    }
    $pythonPathEntries = Get-Content -LiteralPath $pythonPathFile
    $pythonPathEntries = $pythonPathEntries | ForEach-Object {
        if ($_ -eq '#import site') { 'import site' } else { $_ }
    }
    if ($pythonPathEntries -notcontains 'Lib\site-packages') {
        $pythonPathEntries += 'Lib\site-packages'
    }
    $pythonPathEntries | Set-Content -LiteralPath $pythonPathFile -Encoding ASCII

    python -m pip install `
        --disable-pip-version-check `
        --no-compile `
        --only-binary=:all: `
        --target $pythonSitePackages `
        -r (Join-Path $projectRoot 'DataWorker\requirements.txt')
    if ($LASTEXITCODE -ne 0) {
        throw '内置 Python 的 NetCDF 依赖安装失败。'
    }
}

$bundledPython = Join-Path $pythonRuntimeDirectory 'python.exe'
& $bundledPython -c 'import netCDF4, numpy, requests; print(netCDF4.__version__, numpy.__version__, requests.__version__)'
if ($LASTEXITCODE -ne 0) {
    throw '内置 Python 数据运行时自检失败。'
}

$requiredReleaseFiles = @(
    'AISky.Desktop.exe',
    'AISky.Desktop.pri',
    'App.xbf',
    'MainWindow.xbf',
    'MainPage.xbf',
    'SettingsDialog.xbf',
    'UpdateDialog.xbf',
    'AISky.Updater.exe',
    'Python\python.exe',
    'Python\Lib\site-packages\netCDF4\__init__.py',
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

if ($signingEnabled) {
    Write-Host '正在签名 AISky 主程序和更新助手...'
    Invoke-AuthenticodeSign `
        -Path (Join-Path $stageDirectory 'AISky.Desktop.exe') `
        -ToolPath $SignToolPath `
        -Thumbprint $normalizedSigningThumbprint `
        -TimestampServer $TimestampUrl
    Invoke-AuthenticodeSign `
        -Path (Join-Path $stageDirectory 'AISky.Updater.exe') `
        -ToolPath $SignToolPath `
        -Thumbprint $normalizedSigningThumbprint `
        -TimestampServer $TimestampUrl
}

Compress-Archive -Path (Join-Path $stageDirectory '*') -DestinationPath $archivePath
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "sha256:$hash  AISky-Desktop-win-x64.zip" -Encoding ASCII

Write-Host "发布包已生成：$archivePath"
Write-Host "SHA-256：$hash"

if (-not $SkipInstaller) {
    if (-not (Test-Path -LiteralPath $installerScript)) {
        throw "安装器脚本不存在：$installerScript"
    }
    if ([string]::IsNullOrWhiteSpace($InstallerCompilerPath)) {
        $compilerCandidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        )
        $InstallerCompilerPath = $compilerCandidates |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($InstallerCompilerPath) `
        -or -not (Test-Path -LiteralPath $InstallerCompilerPath)) {
        throw @'
没有找到 Inno Setup 命令行编译器 ISCC.exe。
请安装 Inno Setup 7，或使用 -InstallerCompilerPath 指定 ISCC.exe；
仅生成便携版时可显式传入 -SkipInstaller。
'@
    }

    Write-Host "正在生成每用户安装器..."
    $installerCompilerArguments = @(
        '/Qp',
        "/DMyAppVersion=$Version",
        "/DSourceDir=$stageDirectory",
        "/DOutputDir=$OutputDirectory"
    )
    if ($signingEnabled) {
        $innoSignCommand =
            "`"$SignToolPath`" sign /sha1 $normalizedSigningThumbprint " +
            "/fd SHA256 /td SHA256 /tr `"$TimestampUrl`" " +
            "/d `"AISky 桌面气象平台`" `$f"
        $installerCompilerArguments += '/DSigningEnabled=1'
        $installerCompilerArguments += "/Saisky=$innoSignCommand"
    }
    $installerCompilerArguments += $installerScript
    & $InstallerCompilerPath @installerCompilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'AISky 安装器生成失败。'
    }
    if (-not (Test-Path -LiteralPath $installerPath)) {
        throw "安装器编译成功，但没有找到输出：$installerPath"
    }
    if ($signingEnabled) {
        Assert-AuthenticodeSignature -Path $installerPath -ToolPath $SignToolPath
    }
    $installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $installerHashPath `
        -Value "sha256:$installerHash  AISky-Setup-win-x64.exe" `
        -Encoding ASCII
    Write-Host "安装器已生成：$installerPath"
    Write-Host "安装器 SHA-256：$installerHash"
}

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
$releaseAssets = @($archivePath, $hashPath)
if (-not $SkipInstaller) {
    $releaseAssets += $installerPath
    $releaseAssets += $installerHashPath
}
gh release create "v$Version" `
    @releaseAssets `
    --repo $Repository `
    --title $ReleaseTitle `
    --notes-file $NotesFile `
    --latest
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub Release 创建失败。'
}
Write-Host "GitHub Release v$Version 已发布。"
