using WsSim.Core.Game;
using WsSim.Core.Randomness;

namespace WsSim.Core.Agents;

/// <summary>
/// 随机选择的玩家（用于测试和压力测试）。有自己的随机数种子，所以也是确定性的。
/// 有“结束 / 不做”以外的选项时，只以 PassChance 的概率选择不做，避免对局拖得太久。
/// </summary>
public sealed class RandomAgent : IPlayerAgent
{
    private readonly DeterministicRandom _rng;
    public double PassChance { get; init; } = 0.15;

    public RandomAgent(ulong seed) => _rng = new DeterministicRandom(seed);

    public int[] Decide(GameState state, Decision d)
    {
        if (d.MaxPicks == 1 && d.MinPicks == 1)
        {
            var pass = Enumerable.Range(0, d.Options.Count).Where(i => d.Options[i].IsPass).ToList();
            var act = Enumerable.Range(0, d.Options.Count).Where(i => !d.Options[i].IsPass).ToList();
            if (pass.Count > 0 && act.Count > 0)
            {
                bool takePass = _rng.NextInt(1000) < (int)(PassChance * 1000);
                var pool = takePass ? pass : act;
                return new[] { pool[_rng.NextInt(pool.Count)] };
            }
            return new[] { _rng.NextInt(d.Options.Count) };
        }
        // 多选：随机个数，随机子集。
        int n = d.MinPicks + _rng.NextInt(d.MaxPicks - d.MinPicks + 1);
        var idx = Enumerable.Range(0, d.Options.Count).ToList();
        _rng.Shuffle(idx);
        return idx.Take(n).OrderBy(x => x).ToArray();
    }
}

/// <summary>
/// 简单规则的 AI：换掉名场面和高等级卡、优先出高等级角色、能直接攻击就直接攻击、能赢的战斗就正面攻击。
/// 只用于测试“像样的对局”，不是正式的 AI 对手。
/// </summary>
public sealed class SimpleAgent : IPlayerAgent
{
    public int[] Decide(GameState state, Decision d)
    {
        var me = state.Players[d.Player];
        var opp = state.Opponent(d.Player);
        CardInstance? Card(DecisionOption o) =>
            o.CardInstanceId is int id ? FindCard(state, id) : null;

        switch (d.Type)
        {
            case DecisionType.ChooseFirstPlayer:
                return new[] { 0 };

            case DecisionType.Mulligan:
                return Enumerable.Range(0, d.Options.Count)
                    .Where(i => Card(d.Options[i]) is { } c && (c.Def.IsClimax || c.Def.Level >= 2))
                    .ToArray();

            case DecisionType.Clock:
            {
                if (me.Clock.Count >= 5 || me.Hand.Count <= 2) return new[] { PassIndex(d) };
                // 放等级最低的非名场面卡；没有就放名场面。
                int best = -1, bestScore = int.MaxValue;
                for (int i = 0; i < d.Options.Count; i++)
                {
                    var c = Card(d.Options[i]);
                    if (c == null) continue;
                    int score = c.Def.IsClimax ? 100 : c.Def.Level * 10 + c.Def.Cost;
                    if (score < bestScore) { best = i; bestScore = score; }
                }
                return new[] { best >= 0 ? best : PassIndex(d) };
            }

            case DecisionType.MainAction:
            {
                // 把角色放到空格（优先前列），挑等级最高、战力最高的。
                int best = -1;
                (int, int, int) bestKey = (-1, -1, -1);
                for (int i = 0; i < d.Options.Count; i++)
                {
                    var o = d.Options[i];
                    if (o.MainAction != MainActionKind.PlayCharacter) continue;
                    if (me.Stage[(int)o.Slot!.Value] != null) continue;
                    var c = Card(o)!;
                    var key = (o.Slot.Value.IsFront() ? 1 : 0, c.Def.Level, c.Def.Power);
                    if (key.CompareTo(bestKey) > 0) { best = i; bestKey = key; }
                }
                return new[] { best >= 0 ? best : PassIndex(d) };
            }

            case DecisionType.Climax:
                return new[] { 0 };

            case DecisionType.AttackDeclaration:
            {
                int best = -1, bestScore = int.MinValue;
                for (int i = 0; i < d.Options.Count; i++)
                {
                    var o = d.Options[i];
                    if (o.IsPass) continue;
                    var a = me.Stage[(int)o.Slot!.Value]!;
                    var def = opp.Stage[(int)o.Slot.Value.Facing()];
                    int score = o.AttackType switch
                    {
                        AttackType.Direct => 1000,
                        AttackType.Front => a.Power > def!.Power ? 500 : 100,
                        _ => 200 - def!.Level * 50,
                    };
                    if (score > bestScore) { best = i; bestScore = score; }
                }
                return new[] { best >= 0 ? best : PassIndex(d) };
            }

            case DecisionType.Counter:
                return new[] { 0 };

            case DecisionType.Encore:
                return new[] { 0 };

            case DecisionType.LevelUp:
            {
                for (int i = 0; i < d.Options.Count; i++)
                    if (Card(d.Options[i]) is { } c && !c.Def.IsClimax) return new[] { i };
                return new[] { 0 };
            }

            case DecisionType.DiscardToHandLimit:
                return Enumerable.Range(0, d.Options.Count)
                    .OrderBy(i => Card(d.Options[i])!.Def.IsClimax ? 0 : 1)
                    .ThenBy(i => Card(d.Options[i])!.Def.Level)
                    .Take(d.MinPicks).OrderBy(i => i).ToArray();

            default:
                // 判定效果等：选第一个非“不做”的选项。
                for (int i = 0; i < d.Options.Count; i++)
                    if (!d.Options[i].IsPass) return new[] { i };
                return new[] { 0 };
        }
    }

    private static int PassIndex(Decision d)
    {
        for (int i = 0; i < d.Options.Count; i++)
            if (d.Options[i].IsPass) return i;
        return 0;
    }

    private static CardInstance? FindCard(GameState s, int id)
    {
        foreach (var p in s.Players)
        {
            foreach (var z in new[] { p.Deck, p.Hand, p.Clock, p.Level, p.Stock, p.WaitingRoom, p.Memory, p.ClimaxArea, p.Resolution })
                foreach (var c in z)
                    if (c.InstanceId == id) return c;
            foreach (var c in p.Stage)
                if (c != null && c.InstanceId == id) return c;
        }
        return null;
    }
}

/// <summary>按记录的回答重放对局（联机时对手的操作也可以这样喂进引擎）。</summary>
public sealed class ReplayAgent : IPlayerAgent
{
    private readonly Queue<int[]> _answers;

    public ReplayAgent(IEnumerable<int[]> answers) => _answers = new Queue<int[]>(answers);

    public int[] Decide(GameState state, Decision d)
    {
        if (_answers.Count == 0) throw new InvalidOperationException($"回放记录用完了（第 {d.Sequence} 个选择）");
        return _answers.Dequeue();
    }
}
