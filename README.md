# ws_sim

新的 Weiss Schwarz 对战模拟器，由 Claude 与 Loui Shi 共同开发，仅供朋友间非商业使用。版权与归属见开发包的 CREDITS.md。

## 结构

```
WsSim.sln
src/
  WsSim.Core/          规则引擎（.NET 8 类库，不依赖 Godot，客户端与服务端共用）；Hosting/ 是给界面用的后台线程封装
  WsSim.Client/        Godot 4.7 .NET 客户端（人机对战，见该目录 README）
  WsSim.Server/        ASP.NET Core 服务端（P5，占位）
tests/
  WsSim.Core.Tests/    xUnit 测试；TestData/ 内是 NIKKE、SAO 卡片子集和两副测试卡组
docs/
  architecture.md      引擎设计说明
  open-questions.md    规则待确认事项
```

## 开发

需要 .NET 8 SDK。

```
dotnet test
```

客户端需要 Godot 4.7.2 .NET 版，运行方法见 [src/WsSim.Client/README.md](src/WsSim.Client/README.md)。

开发包 `ws-devkit/` 不进仓库。把它放在仓库根目录（或设置环境变量 `WS_DEVKIT` 指向它），测试会改为读取完整的 `ws-devkit/data/cards.json`；没有时使用 `tests/WsSim.Core.Tests/TestData/` 里的子集。

## 阶段

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| P0 | 开发包 | 完成 |
| P1 | 规则引擎骨架（不含卡片效果） | 完成 |
| P2 | Godot 客户端（人机对战） | 完成 |
| P3 | 效果脚本解释器 + 界面改版（实卡布局、动画、菜单） | 进行中 |
| P4 | Steam 联机 | |
| P5 | 服务端 | |
| P6 | 扩展 | |
