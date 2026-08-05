# AISky 0.8.0 第八阶段测试报告

测试环境：Windows x64、Release 构建、全新解压目录、隔离数据目录，以及发行包
内置 Python 3.11。测试数据为脚本生成的小型 NetCDF，不包含业务预报数据。

| 项目 | 结果 | 验证方式 |
|---|---|---|
| Release 编译 | 通过 | x64 Release 构建 0 错误、0 警告 |
| JavaScript 语法 | 通过 | `node --check MapHost/map.js` |
| 发行包完整性 | 通过 | ZIP 的 SHA-256 与随包校验文件一致 |
| 独立解压启动 | 通过 | 从全新目录启动，默认最大化并正确显示首次使用引导 |
| 内置运行环境 | 通过 | 包内 Python 3.11.9、netCDF4 1.7.4、NumPy 2.4.6、requests 2.34.2 可用 |
| 数据管线 | 通过 | 包内工作进程通过下载、同步清理和容错三组自动测试 |
| 首次导入 | 通过 | 隔离数据目录导入 NetCDF 后生成 8 个图层并进入完整地图 |
| 图层联动 | 通过 | T2M 切换至 WIND10 后，地图填色、色带和风场流线同步更新 |
| 地图底图 | 通过 | 国界、省界、河流、湖泊、经纬网和城市标注正常显示 |
| 响应式布局 | 通过 | 最大化窗口内顶部选择器、右侧面板、色带和时间轴无重叠 |
| 单实例 | 通过 | 再次启动 EXE 后新进程自动退出，只保留并恢复已有窗口 |
| 错误恢复 | 通过 | 错误密码、网络重试、损坏 NetCDF 和损坏数据库测试均通过 |
| 更新配置 | 通过 | 发行包指向 `zhangxutao3/AISky`，版本文件与 EXE 均为 0.8.0 |

发行包：

```text
AISky-Desktop-win-x64.zip
SHA-256: 9bc2ec12c815e030de874e6285de3a683f456cce6f26dd2c7be2727613182ab6
```

数据管线一键测试：

```powershell
.\scripts\Test-AISky.ps1 `
  -Python .\artifacts\release-smoke-v0.8.0\Python\python.exe
```

发行包额外使用其自身的 `DataWorker/worker.py`、数据库结构和内置 Python
执行同一组测试，避免依赖开发机的 Python 环境。
