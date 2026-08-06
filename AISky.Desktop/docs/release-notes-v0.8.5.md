# AISky 0.8.5

Windows 安装版与软件内升级支持。

## 安装体验

- 新增单文件 `AISky-Setup-win-x64.exe`，普通用户不再需要手动解压
- 默认按当前用户安装，不需要管理员权限
- 自动创建开始菜单入口，可选择创建桌面快捷方式
- 支持从 Windows“已安装的应用”中完整卸载
- 继续保留 `AISky-Desktop-win-x64.zip` 便携版

## 软件更新

- 安装版自动下载下一版本安装器，静默升级后重新启动 AISky
- 便携版继续使用失败可恢复的 ZIP 目录替换
- 两种版本均验证 GitHub 文件大小与 SHA-256
- 安装、升级和卸载不会删除 NetCDF、索引、设置或日志

## 下载提示

- 本次 GitHub Release 按项目所有者的选择以未签名方式发布
- Windows 首次安装或启动时可能显示“未知发布者”或 Microsoft Defender SmartScreen 提示
- 请仅从本项目的 GitHub Releases 页面下载，并使用随附的 SHA-256 文件校验完整性
- 发布脚本保留 Authenticode、RFC 3161 时间戳和签名复验能力，后续取得可信证书后可直接启用
