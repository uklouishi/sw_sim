# WsSim.Client

Godot 4.7 .NET（C#）客户端。规则全部由 `WsSim.Core` 计算，客户端只负责显示和收集玩家的选择。

## 运行

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 **Godot 4.7.2 .NET 版**（下载页面里带 “.NET” 的那个；版本要和 `WsSim.Client.csproj` 里的 `Godot.NET.Sdk/4.7.2` 一致）。
2. 用 Godot 编辑器“导入”本文件夹的 `project.godot`，按 F5 运行（第一次会自动编译 C#）。
3. 开始菜单里设置两个目录（会记住）：
   - **开发包目录**：`ws-devkit` 文件夹（里面有 `data/cards.json`）。放在仓库根目录、或设置环境变量 `WS_DEVKIT` 时会自动找到。
   - **卡图目录**：原模拟器的 `…/Weiss Schwarz 0.6.5.3 Windows/Weiss Schwarz_Data/StreamingAssets/Cards`。装在 D 盘或 C 盘根目录时会自动找到；没有卡图时用文字卡面显示。
4. 界面素材（可选）放在开发包的 `assets/` 文件夹，缺的会用代码画的版本代替：

   | 文件 | 用途 |
   | --- | --- |
   | `ws-cardback.png` | 卡背（原模拟器 `sharedassets0.assets` 里的 `ws-cardback`，已提取） |
   | `playmat.png` | 对局场地背景（16:9，不要有文字和区域框，区域框由程序画） |
   | `menu-bg.png` | 菜单背景 |
   | `emblem.png` | 菜单标题旁的徽章（透明背景） |

5. 选择双方卡组后开始对局。卡组来自开发包 `tests/decks/`、原模拟器 `StreamingAssets/Decks/`，以及 `user://decks/`（Windows 上是 `%APPDATA%\Godot\app_userdata\WS Sim\decks\`），格式都是每行一个卡号。

## 场地

按 WS 实卡场地布局，我方在下、对手在上并旋转 180°（面对面）：前列 3 格靠近中线，后列 2 格在后面（④在①②之间、⑤在②③之间）；
名场面区在前列左边（横放）；能量区在左侧（背面横放、竖着叠）；等级区在左下（横放）；伤害区在下方横排 7 格；
牌库、休息室在右侧，思出区在右下；判定区在牌库旁边，放结算中的卡（伤害翻牌、判定）。
我方前列左格正对对手前列右格（综合规则 3.6.6）。具体坐标见 `src/BoardLayout.cs`。

## 动画

引擎每一步变化都发事件（`WsSim.Core.Game.GameEvent`：卡移动、横置 / 倒置、洗牌、攻击宣告、阶段开始），界面排队逐个播放：
卡从原位置飞到新位置（看不到正面的一端是卡背，途中翻面），横置 / 倒置转动，攻击画箭头，回合开始显示横幅。
播放期间后面的事件排队等待，所以对手的每一步都看得清。菜单和对局右下角可以调动画速度（关闭 / 快 / 标准 / 慢）。

## 操作

- 可以操作的卡有**黄框**。点一下：只有一种做法就直接执行；有多种做法时这张卡变**蓝框**，可以去的格子变**绿框**，点格子完成（出角色、移动），或者点右侧的按钮（攻击方式等）。
- 换牌、弃牌这类多选：点卡切换选中，再按“确定”。
- 右侧按钮列出当前所有选项，任何选择都可以直接点按钮完成；主要阶段默认只列“结束”，按“列出全部行动”可展开。
- 鼠标移到卡上，右上角显示大图、数值和效果文字（简体中文，没有时显示英文）。
- 点休息室 / 思出区查看全部卡。箭头判定等需要从休息室选卡时会自动打开。

对手由 `SimpleAgent`（简单规则 AI）操作。菜单里可勾选“观战”，双方都由 AI 操作。

## 结构

```
project.godot          Godot 项目（GL Compatibility 渲染器，1600×900）
scenes/Main.tscn       只有根节点，界面全部在代码里搭
src/Main.cs            根节点：设置、卡片库、菜单 / 对局切换、命令行参数
src/MenuScreen.cs      开始菜单（DeckPicker.cs 选卡组，MenuBackground.cs 背景）
src/GameScreen*.cs     对局界面：事件处理、场地（.Board）、动画（.Anim）、点击（.Input）、详情与日志（.Detail）、自检（.SelfTest）
src/BoardLayout.cs     场地各区域的坐标（实卡布局，对手旋转 180°）
src/PlaymatControl.cs  场地底图和区域框
src/CardControl.cs     一张卡 / 一个空格的绘制（卡图或文字卡面，横置 90°、倒置 180°，名场面横图）
src/Assets.cs          开发包 assets/ 里的界面素材
src/CardLibrary.cs     读 cards.json、简体中文效果文字、卡图缓存、卡组列表
src/Settings.cs        user://settings.cfg
```

引擎在后台线程运行（`WsSim.Core.Hosting.GameHost`），界面每帧从事件队列取快照（`GameView`），不直接读引擎的 `GameState`。

## 命令行（自动检查用）

Godot 参数后面加 `--`，再加：

| 参数 | 作用 |
| --- | --- |
| `--play` / `--auto` | 卡片读好后直接开一局人机 / 观战 |
| `--seed N` | 指定种子 |
| `--selftest [N]` | 模拟点击代替玩家做前 N 个选择（不写 N 就打完整局），每一步检查点击能对应到选项 |
| `--ai-delay 毫秒` | 对手操作间隔 |
| `--anim 0-3` | 动画速度（0 关闭、1 快、2 标准、3 慢）；自检默认关闭动画，指定后自检也播动画 |
| `--devkit 路径` / `--images 路径` | 覆盖设置里的目录 |
| `--shot 文件.png --shot-delay 秒` | 到时截图并退出 |
| `--shot-count N --shot-every 秒` | 连拍 N 张（文件名加 `_000` 序号），检查动画用 |

例：`godot --path src/WsSim.Client -- --play --selftest --ai-delay 0 --seed 5`
