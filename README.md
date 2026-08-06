# AISky Desktop

AISky 是用于展示本地 NetCDF 气象预报产品的 Windows 桌面可视化软件，
基于 C#、WinUI 3 和 Windows App SDK 构建。

0.9.3 提供 Energy / SDS 本地模式台风路径识别、曲线与路径点跳时刻、完整深色
序列面板，以及弱风长尾、强风短尾且由明亮头部向透明尾部衰减的风粒子动画。

## 下载

普通用户请从仓库的 **Releases** 页面下载
`AISky-Setup-win-x64.exe`。安装程序会创建开始菜单入口和可选的桌面快捷方式，
以后可直接在 AISky 内检查并安装更新。

需要免安装运行时，也可以下载 `AISky-Desktop-win-x64.zip`，
解压后运行 `AISky.Desktop.exe`。两个发布包均为 x64 自包含版本，
无需另行安装 .NET 或 Windows App SDK。
从 0.7.0 起还内置 Python 数据运行时，无需用户安装 Python。

## 源码目录

- `AISky.Desktop/`：主程序、地图宿主、数据处理器和发布脚本
- `AISky.Updater/`：独立更新助手
- `installer/`：每用户安装器定义
- `AISky.Desktop/docs/RELEASING.md`：版本发布说明
- `AISky.Desktop/docs/USER_GUIDE.md`：普通用户使用说明

详细开发与运行方法请阅读
[`AISky.Desktop/README.md`](AISky.Desktop/README.md)。
