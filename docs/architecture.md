# 规则引擎设计（P1）

## 运行方式

`GameEngine` 同步运行一整局：`new GameEngine(deck0, deck1, agent0, agent1, seed).Run()`。

- **选择**：引擎需要玩家决定时构造一个 `Decision`（类型、提示、选项列表、最少/最多选几个），交给该玩家的 `IPlayerAgent`，得到选项序号数组。回答只含序号，可直接序列化。界面、AI、网络对手、回放都实现 `IPlayerAgent`。
- **随机**：所有随机（猜拳、洗牌）只走 `DeterministicRandom`（xoshiro256**），不用 `System.Random`，保证不同 .NET 版本、不同机器结果相同。
- **确定性**：同一种子 + 同一串回答 = 完全相同的对局。`GameEngine.Decisions` 是回答记录，用 `ReplayAgent` 可重放；`StateHash()` 是状态哈希（FNV-1a），联机时双方逐步比对检测不同步。
- **不使用**无序集合的遍历顺序决定结果；同时处理时按“回合玩家先”的固定顺序。

## 区域约定

- `Deck[0]` 是牌库顶；`Clock`、`Stock`、`Level`、`WaitingRoom` 的列表末尾是最上面（最新放入）。支付能量从末尾取。
- 舞台 5 格：前列 0–2、后列 3–4；我方前列第 i 格正对对手前列第 i 格。
- 每张卡在一局里有唯一的 `InstanceId`，由开局卡组顺序决定。

## 规则处理时点（CheckTiming）

洗牌重整 → 升级 → 重整伤害 → 败北判定，循环到没有可处理的事项为止，回合玩家先。牌库在任何时候变成 0 张会立即洗牌重整，重整伤害留到下一个规则处理时点。

## P2 预留

卡片效果脚本在 P2 接入：`CardDefinition.ScriptExpanded` 是输入；触发时机（登场、攻击、判定、倒置等）将在引擎对应位置发出事件。
