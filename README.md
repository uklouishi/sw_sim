# ws_sim

新的 Weiss Schwarz 对战模拟器，由 Claude 与 Loui Shi 共同开发，仅供朋友间非商业使用。版权与归属见开发包的 CREDITS.md。

## 结构

```
WsSim.sln
src/
  WsSim.Core/          规则引擎（.NET 8 类库，不依赖 Godot，客户端与服务端共用）
  WsSim.Client/        Godot 4 .NET 客户端（P3，占位）
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

开发包 `ws-devkit/` 不进仓库。把它放在仓库根目录（或设置环境变量 `WS_DEVKIT` 指向它），测试会改为读取完整的 `ws-devkit/data/cards.json`；没有时使用 `tests/WsSim.Core.Tests/TestData/` 里的子集。

## 阶段

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| P0 | 开发包 | 完成 |
| P1 | 规则引擎骨架（不含卡片效果） | 进行中 |
| P2 | 效果脚本解释器 | |
| P3 | Godot 客户端 | |
| P4 | Steam 联机 | |
| P5 | 服务端 | |
| P6 | 扩展 | |
