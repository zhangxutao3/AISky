# AISky 发布与更新指南

AISky 同时发布“Windows x64 每用户安装器”和绿色免安装包。两个包均已包含 .NET、
Windows App SDK 与 Python/NetCDF 数据运行时。普通用户优先使用
`AISky-Setup-win-x64.exe`，便携使用时再选择 ZIP。

## 第一次配置更新仓库

1. 在 GitHub 创建用于发布 AISky 的仓库。
2. 记下仓库地址中的 `owner/repo`，例如 `my-name/aisky-desktop`。
3. 安装 GitHub CLI，并在 PowerShell 中运行：

```powershell
gh auth login
```

源码中的 `Config/update-config.json` 可以保持空白。发布脚本收到
`-Repository owner/repo` 后，会只修改发布包内的配置，不会改乱源码。

## 生成安装版和绿色发布包

在 `AISky.Desktop` 目录运行：

```powershell
.\scripts\Publish-AISky.ps1 `
  -Version 0.7.0 `
  -Repository my-name/aisky-desktop
```

输出位于：

```text
..\artifacts\v0.7.0\AISky-Setup-win-x64.exe
..\artifacts\v0.7.0\AISky-Desktop-win-x64.zip
```

同目录还会分别生成 `.sha256` 校验文件。安装器编译需要 Inno Setup 6.7+ 或 7；可以用
`-InstallerCompilerPath` 指定 `ISCC.exe`。只有明确需要便携包时，才使用
`-SkipInstaller` 跳过安装器。脚本拒绝覆盖已经存在的版本目录，避免误删旧包。

网络不稳定时，可以复用之前成功发布时保存的同版本 Python 嵌入包：

```powershell
.\scripts\Publish-AISky.ps1 `
  -Version 0.8.0 `
  -Repository my-name/aisky-desktop `
  -PythonArchivePath ..\artifacts\v0.7.0\python-3.11.9-embed-amd64.zip
```

日常补丁发布可以直接复用上一版已经通过自检的完整 Python 目录，省去重复安装依赖；
脚本仍会在打包前再次执行运行时自检：

```powershell
.\scripts\Publish-AISky.ps1 `
  -Version 0.8.2 `
  -Repository my-name/aisky-desktop `
  -PythonRuntimePath ..\artifacts\release-smoke-v0.8.0-final\Python
```

开发过程中的快速数据回归只运行基础下载、解析和索引测试：

```powershell
..\scripts\Test-AISky.ps1 -Quick
```

正式发布前仍需省略 `-Quick`，执行全部同步、清理和容错测试。

## Authenticode 签名

公开发布默认要求代码签名证书。证书必须安装在当前用户或本地计算机的 Personal
证书存储中，包含私钥和 Code Signing 扩展密钥用途。发布脚本会签名主程序、更新助手、
安装程序和卸载程序，并使用 RFC 3161 时间戳，然后调用 SignTool 再次验证。

```powershell
.\scripts\Publish-AISky.ps1 `
  -Version 0.8.5 `
  -Repository zhangxutao3/AISky `
  -SigningCertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

`-Upload` 在没有证书时会直接停止，防止误发“未知发布者”版本。如果确实要发布未签名
测试版，必须显式传入 `-AllowUnsignedRelease`；该参数不应用于面向普通用户的正式版。

## 打包并发布到 GitHub Release

先复制 `release-notes.example.md`，填写本次更新内容，然后运行：

```powershell
.\scripts\Publish-AISky.ps1 `
  -Version 0.6.1 `
  -Repository my-name/aisky-desktop `
  -ReleaseTitle "AISky 0.6.1" `
  -NotesFile .\docs\release-notes-0.6.1.md `
  -Upload
```

脚本会：

1. 发布 AISky 主程序和独立更新助手。
2. 生成名称固定的 `AISky-Setup-win-x64.exe` 和 `AISky-Desktop-win-x64.zip`。
3. 分别计算 SHA-256。
4. 创建 `v0.6.1` GitHub Release。
5. 上传安装器、压缩包和校验文件，并标记为最新版本。

## 用户端更新流程

用户点击“检查软件更新”后，AISky 会读取最新正式 Release。安装版选择
`AISky-Setup-win-x64.exe`，便携版选择 `AISky-Desktop-win-x64.zip`。发现新版本时
显示版本号、UTC 发布时间、更新说明和下载大小。下载完成后先校验文件大小及 GitHub
提供的 SHA-256，再启动独立更新助手。

安装版由更新助手等待 AISky 完全退出后静默运行安装器，更新快捷方式和卸载信息，再重启
AISky。便携版继续使用失败可恢复的目录替换。用户的 NetCDF、索引、设置和日志均在
AISky 数据目录中，不在程序目录内，因此两种升级均不会覆盖这些数据。

## 版本号规则

版本号使用 `主版本.次版本.修订号`：

- `0.6.1`：错误修复或小改进。
- `0.7.0`：加入一组新功能。
- `1.0.0`：首个正式稳定版本。

GitHub 标签请使用 `v0.6.1`。Release 资产名称必须保持
`AISky-Setup-win-x64.exe` 和 `AISky-Desktop-win-x64.zip`，否则对应版本的客户端
会提示找不到 Windows 更新包。
