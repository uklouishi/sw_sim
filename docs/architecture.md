# 规则引擎设计（P1）

## 运行方式

`GameEngine` 同步运行一整局：`new GameEngine(deck0, deck1, agent0, agent1, seed).Run()`。

- **选择**：引擎需要玩家决定时构造一个 `Decision`（类型、提示、选项列表、最少/最多选几个），交给该玩家的 `IPlayerAgent`，得到选项序号数组。回答只含序号，可直接序列化。界面、AI、网络对手、回放都实现 `IPlayerAgent`。
- **随机**：所有随机（猜拳、洗牌）只走 `DeterministicRandom`（xoshiro256**），不用 `System.Random`，保证不同 .NET 版本、不同机器结果相同。
- **确定性**：同一种子 + 同一串回答 = 完全相同的对局。`GameEngine.Decisions` 是回答记录，用 `ReplayAgent` 可重放；`StateHash()` 是状态哈希（FNV-1a），联机时双方逐步比对检测不同步。
- **不使用**无序集合的遍历顺序决定结果；同时处理时按“回合玩家先”的固定顺序。

## 区域约定

- `Deck[0]` 是牌库顶；`Clock`、`Stock`、`Level`、`WaitingRoom` 的列表末尾是最上面（最新放入）。支付能量从末尾取。
- 舞台 5 格：前列 0–2、后列 3–4，左右按该玩家自己的视角。我方前列左格正对对手前列右格（综合规则 3.6.6），用 `StageSlot.Facing()` 取正对的格子。
- 每张卡在一局里有唯一的 `InstanceId`，由开局卡组顺序决定。

## 规则处理时点（CheckTiming）

洗牌重整 → 升级 → 重整伤害 → 败北判定，循环到没有可处理的事项为止，回合玩家先。牌库在任何时候变成 0 张会立即洗牌重整，重整伤害留到下一个规则处理时点。

## 给界面用：GameHost（Hosting/）

`GameEngine.Run()` 是同步的、会阻塞在 `IPlayerAgent.Decide` 上，所以客户端用 `GameHost` 在后台线程运行它：

- 每个座位传一个 agent；传 `null` 表示由界面操作。问到界面座位时，`GameHost` 发出 `DecisionRequired` 事件并阻塞，界面调用 `Submit(序号, 回答)` 后继续。回答不合法时 `Submit` 返回 false，不改变任何东西。
- 问到 AI 座位时先发 `StateUpdated`，可选等待 `AiDelay`（让玩家看清对手的操作）。
- 每个事件带一个 `GameView`：某一玩家视角的不可变快照，在引擎线程上、引擎停在提问处时生成。对手手牌、双方能量区、牌库只给张数（隐藏的卡 `InstanceId` 为 -1，因为 InstanceId 由开局卡组顺序决定，会泄露是哪张卡）。`NewLog` 是上次快照之后的新日志。
- 界面线程只读快照，从不碰 `GameState`。`GameHost` 不引入任何随机：同样的种子和回答，结果和直接跑引擎完全一样（有测试）。
- `StepEvents = true` 时，引擎每一步变化（`GameEngine.EventRaised`：卡移动、方向改变、洗牌、攻击宣告、阶段开始）都发一个 `BoardChanged`，带移动后的快照，界面据此播动画。两端都看不到正面的移动（对手抽牌等）会去掉 InstanceId。
- 联机（P4）时对手座位可以换成接收网络回答的 agent，服务端（P5）也可以直接复用。

## 卡片效果预留

卡片效果脚本在 P3 接入：`CardDefinition.ScriptExpanded` 是输入；触发时机（登场、攻击、判定、倒置等）将在引擎对应位置发出事件。
