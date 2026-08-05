# AISky Desktop

AISky 本地气象预报可视化软件的 WinUI 3 桌面端工程。

## 打开与运行

初学者推荐直接用 Visual Studio 打开 `AISky.slnx`，将启动项目设为
`AISky.Desktop`，选择 `x64` 后按 `F5`。

也可以在当前目录运行：

```powershell
dotnet build .\AISky.Desktop.csproj -c Debug -p:Platform=x64
.\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AISky.Desktop.exe
```

配置和日志保存在：

```text
%LOCALAPPDATA%\AISky\Desktop\
```

## 源码开发准备

源码调试时，NetCDF 解析与下载由隔离的 Python 进程完成。首次调试前安装依赖：

```powershell
python -m pip install -r .\DataWorker\requirements.txt
```

正式绿色发布包已经内置 Python、NumPy、netCDF4 和 requests，普通用户无需安装
Python 或 Windows App SDK，解压后直接运行即可。

程序默认使用 UTC 显示起报和预报时刻；可在“更多 → 显示时区”统一切换为
UTC+8 等显示时区，原始数据仍按 UTC 保存。

## 已实现

- AISky-Energy / AISky-SDS 模型、起报时刻和预报时刻联动选择
- NetCDF4 文件完整性校验、变量读取和二维场渲染缓存
- 实际经纬网、海岸线、城市标注、鼠标平移/滚轮缩放与格点取值
- 根据文件内容动态生成图层列表、图例单位和值域
- 多预报时次时间轴选择和循环播放
- 地图键盘快捷操作，以及播放时仅更新当前时间格和格点曲线指示点
- 公开直连或密码分享页后台补数、日期范围选择、右上角进度、失败统计和取消
- 下载后自动校验、SQLite WAL 索引刷新与界面更新
- “更多”菜单导入本地 `.nc` 文件，便于离线检查自己的预报产品
- 可持久化的自动同步开关；开启时立即检查两个模型，之后每 3 小时检查最新固定起报
- 手动“立即同步”，同步过程在后台执行并在完成后刷新当前地图索引
- 关闭主窗口时最小化到 Windows 通知区，双击图标恢复窗口
- 托盘右键菜单：打开、立即同步、暂停/开启自动同步、检查更新、退出
- 托盘图标按正常、同步中和错误三种状态变化，并提供后台状态提醒
- 3 / 7 / 15 / 30 天或自定义缓存保留期，以及设置页中的安全手动清理
- 同步与清理失败不会终止应用，详细信息写入本地日志并等待下次重试
- 软件版本号、GitHub Release 更新检查、发布说明和下载大小展示
- 更新包流式下载、文件大小与 SHA-256 校验，以及失败可恢复的独立更新助手
- 可选的登录 Windows 后在通知区启动
- 一键生成绿色免安装包并通过 GitHub CLI 创建 Release 的发布脚本
- 游戏启动界面式首次封面、120 时次初始化进度，以及可选的数据访问密码
- 下载断线自动重试、损坏 NetCDF 隔离、损坏 SQLite 自动备份重建
- 内置数据运行时，发布包不再依赖电脑预装 Python

应用数据目录：

```text
%LOCALAPPDATA%\AISky\Desktop\data      原始 NetCDF
%LOCALAPPDATA%\AISky\Desktop\cache     SQLite 索引和地图渲染缓存
%LOCALAPPDATA%\AISky\Desktop\logs      运行日志
```

补数过程使用 `.part` 临时文件，只有完整且通过校验后才会入库。网络中断后的字节级
断点续传不能保证；再次运行会跳过已完成文件并重新尝试未完成项。

自动同步默认为关闭，避免首次运行时意外下载较多数据；可在顶部同步按钮或
“设置 → 后台与缓存”中开启。开启后会立即检查 Energy 与 SDS，跳过本地已有的
近期完整模型，并为缺失或陈旧模型同步未来 15 天的 120 个预报时次；之后每 3 小时
再次检查。同步进度仅在任务运行时显示。

“关闭主窗口后继续运行”默认开启。若要彻底退出，请使用托盘右键菜单中的
“退出 AISky”，或先在设置中关闭该选项。

当前程序版本为 `0.8.4`，更新源为
[`zhangxutao3/AISky`](https://github.com/zhangxutao3/AISky)。
点击“检查软件更新”会读取该仓库的最新 GitHub Release，并匹配
`AISky-Desktop-win-x64.zip` 更新包。发布新版本请参考
[`docs/RELEASING.md`](docs/RELEASING.md)。

普通用户请阅读 [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md)，测试结果见
[`docs/TEST_REPORT-v0.8.4.md`](docs/TEST_REPORT-v0.8.4.md)。
