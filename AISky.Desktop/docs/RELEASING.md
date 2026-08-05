# AISky 发布与更新指南

AISky 当前采用“Windows x64 绿色免安装包”发布方式。发布包已经包含 .NET 与
Windows App SDK 与 Python/NetCDF 数据运行时，用户解压后可直接运行
`AISky.Desktop.exe`。

## 第一次配置更新仓库

1. 在 GitHub 创建用于发布 AISky 的仓库。
2. 记下仓库地址中的 `owner/repo`，例如 `my-name/aisky-desktop`。
3. 安装 GitHub CLI，并在 PowerShell 中运行：

```powershell
gh auth login
```

源码中的 `Config/update-config.json` 可以保持空白。发布脚本收到
`-Repository owner/repo` 后，会只修改发布包内的配置，不会改乱源码。

## 只生成绿色发布包

在 `AISky.Desktop` 目录运行：

```powershell
.\scripts\Publish-AISky.ps1 `
  -Version 0.7.0 `
  -Repository my-name/aisky-desktop
```

输出位于：

```text
..\artifacts\v0.7.0\AISky-Desktop-win-x64.zip
```

同目录还会生成 `.sha256` 校验文件。脚本拒绝覆盖已经存在的版本目录，避免误删旧包。

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
  -Version 0.8.1 `
  -Repository my-name/aisky-desktop `
  -PythonRuntimePath ..\artifacts\release-smoke-v0.8.0-final\Python
```

开发过程中的快速数据回归只运行基础下载、解析和索引测试：

```powershell
..\scripts\Test-AISky.ps1 -Quick
```

正式发布前仍需省略 `-Quick`，执行全部同步、清理和容错测试。

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
2. 生成名称固定的 `AISky-Desktop-win-x64.zip`。
3. 计算 SHA-256。
4. 创建 `v0.6.1` GitHub Release。
5. 上传压缩包和校验文件，并标记为最新版本。

## 用户端更新流程

用户点击“检查软件更新”后，AISky 会读取最新正式 Release。发现新版本时显示版本号、
UTC 发布时间、更新说明和下载大小。下载完成后先校验文件大小及 GitHub 提供的
SHA-256，再启动独立更新助手。

更新助手会等待 AISky 完全退出，将旧程序目录改名为带时间戳的备份目录，再放入新版本。
如果替换失败且旧目录已经移动，会自动恢复旧版本。用户的 NetCDF、索引、设置和日志均在
`%LOCALAPPDATA%\AISky\Desktop`，不在程序目录中，因此升级不会覆盖这些数据。

## 版本号规则

版本号使用 `主版本.次版本.修订号`：

- `0.6.1`：错误修复或小改进。
- `0.7.0`：加入一组新功能。
- `1.0.0`：首个正式稳定版本。

GitHub 标签请使用 `v0.6.1`，Release 压缩包名称保持
`AISky-Desktop-win-x64.zip`，否则客户端会提示找不到 Windows 更新包。
