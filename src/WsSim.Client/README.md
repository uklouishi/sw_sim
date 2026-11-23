# WsSim.Client（P3）

Godot 4 .NET（C#）客户端，P3 阶段在这里创建 Godot 项目。

- 引用 `../WsSim.Core/WsSim.Core.csproj`，规则全部由 Core 计算，客户端只负责显示和收集玩家选择（实现 `IPlayerAgent`）。
- 卡图运行时从玩家电脑上的原模拟器安装目录读取（`StreamingAssets/Cards/` + cards.json 的 `image`），不打包进程序。
- 界面默认简体中文，可切换英文。
