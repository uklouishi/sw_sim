# 效果脚本解释器（P3）

卡片效果来自开发包 `cards.json` 的 `script` 字段（已展开通用宏，见 `CardDefinition.ScriptExpanded`）。语法见开发包 `spec/dsl-grammar.md`，指令清单见 `spec/dsl-commands.md`。原模拟器没有语法文档，下面的语义是对照卡面英文和脚本归纳出来的，和原模拟器不一致的地方请记到 `docs/open-questions.md`。

`GameOptions.EnableEffects = false` 时不执行脚本，回到 P1 的纯规则引擎。

## 代码位置

| 文件 | 内容 |
| --- | --- |
| `Effects/Script.cs`、`Effects/ScriptParser.cs` | 脚本 → 能力列表（`Auto:` / `Cont:` / `Act:` / `Effect:` + 块 + 修饰），按卡缓存 |
| `Effects/Conditions.cs` | 条件表达式解析（`&&&` → `&&` / `||` → 原子） |
| `Effects/Runtime.cs` | 有期限的修正、【永】层、执行上下文、待处理的【自】 |
| `Game/GameEngine.Conditions.cs` | 条件判定、数量（X、ThatCardsLevel、CardsInClock…）、区域名 |
| `Game/GameEngine.Costs.cs` | 费用解析和支付（PayCost、【起】的头、再演） |
| `Game/GameEngine.Commands.cs` | 指令执行 |
| `Game/GameEngine.Abilities.cs` | 【自】的触发和处理、【永】的计算、【起】、事件卡、预约效果 |
| `Effects/ScriptCoverage.cs` | 静态检查脚本里有没有还不认识的东西 |

## 执行模型

- **【自】**：引擎在登场、攻击、倒置、离开舞台、阶段开始等位置调用 `FireFor`，把对上时机的能力放进待处理列表。下一个规则处理时点（`CheckTiming`）先做规则处理，再一个一个处理【自】：回合玩家的先，同一玩家有多个时让玩家选顺序（`DecisionType.ChooseAbility`）。“每回合 N 次”按卡、按能力计数。
- **【永】**：每次提问前、战斗和伤害计算前、规则处理后全部重新计算（`UpdateContinuous`，两遍，让【永】给的【永】也生效）。结果写在每张卡的 `Cont` 层，不留日志。
  - `GiveSelf`：给自己。块里的 `TargetLocation` / `NarrowTargets` 只用来计数和判断，`GainPower` 等始终给自己（例：“其他 NIKKE 每有 1 个 +1000”）。
  - `GiveAll`：舞台上的角色或名场面区的名场面，给我方每个角色（块里的“这张卡”是受影响的角色，`IfOwner` 判断来源）。
  - `Assist`：后列角色给前方的角色。`GiveFacing`：给正对的角色。`InHand`：在手牌时（降等级等）。`Global` / `Memory`：给块里选的目标。
- **【起】**：主要阶段的行动里列出能发动的（费用付得起、没超次数、满足 `ActivationRequirement`）。
- **事件卡**：使用时执行 `Effect:` 块，`Requirement` 是使用条件。有 `Counter` 标记的事件在反击步骤使用。
- **再演**：`Auto: Encore <费用>` 和规则再演（3 能量）一起作为选项。

### 目标

指令默认作用于“这张卡”。`TargetLocation` / `TargetTop` / `Peek` / `SearchLocationForTargets` 等设置目标，`NarrowTargets` 筛选，`Choose…FromTargets` 让玩家选，之后的 `GainPower`、`MoveCard` 等作用于目标。`SaveTargets` / `LoadTargets` / `PushTargets` 保存和恢复。`Peek` / `RevealUpTo` 看过的卡记在“已查看”里，`PutTheRestIntoTheWaitingRoom` 把还在牌库里的放到休息室。

### 有期限的效果

`GainPower` 等在【自】/【起】/事件里是“直到回合结束”，前面有 `UntilEndOfOpponentsTurn` 时是“直到对手的回合结束”（只对下一条生效）。卡换区域时修正全部清除（规则上是新的卡）。

### 选择

新增的选择类型：`ChooseCards`（选多张，个数在 MinPicks–MaxPicks）、`ChooseOption`（多选一）、`ChooseAbility`（先处理哪个【自】）。`DecisionOption.Card` 带卡的定义，界面可以显示牌库里查看的卡。主要阶段新增 `MainActionKind.Activate`（发动【起】，`AbilityIndex` 是能力序号）。

## 覆盖率

“认识”= 脚本里的每条指令、时机、【永】类型、条件、费用解释器都有对应处理（`ScriptCoverage.Analyze`）。这是静态检查，不代表每张卡的行为都和原模拟器一致。

| 范围 | 全部认识 |
| --- | --- |
| NIKKE（NIK/） | 135 / 138 |
| SAO（SAO/） | 860 / 874 |
| 全部卡片 | 36,474 / 39,244（93%）|

NIKKE / SAO 还不认识的：`Flicker`（暂时移出舞台再放回）、`HandAction`（替代费用使用）、`OverrideTriggerIconEffect`（改写判定图示效果）、`StockSwap`、`Auto: OnUseAccelerate`、`BecomeNewBattleOpponent`、`PlayClimax`，以及两张脚本本身有错的卡。遇到不认识的指令时跳过这一条（日志里记一次 `[脚本]`），对局不会中断。

## 测试

- `EffectStressTests.Coverage_Report`：打印覆盖率和缺少的指令（`dotnet test --filter Coverage_Report --logger "console;verbosity=detailed"`）。
- `EffectGameTests`：NIKKE / SAO 随机组卡组，随机 AI 和简单 AI 各打 60 局 × 4 组，检查不崩溃、卡的总数不变。
- `CardEffectTests`：具体卡片（名场面全体加成、应援、再演费用、集中、攻击时支付费用、【永】计数、正对角色条件）。
