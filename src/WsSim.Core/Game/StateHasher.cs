using WsSim.Core.Randomness;

namespace WsSim.Core.Game;

/// <summary>
/// 对局状态哈希（FNV-1a 64 位）。覆盖回合、阶段、每个区域里卡的顺序与状态、随机数状态。
/// 联机时双方每步比对，用来检测不同步。
/// </summary>
public static class StateHasher
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Compute(GameState s, DeterministicRandom? rng = null)
    {
        ulong h = Offset;
        void Mix(long v)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    h ^= (byte)(v >> (i * 8));
                    h *= Prime;
                }
            }
        }

        Mix(s.TurnNumber);
        Mix(s.ActivePlayer);
        Mix(s.FirstPlayer);
        Mix((int)s.Phase);
        Mix(s.AttacksThisTurn);
        Mix(s.IsOver ? 1 : 0);
        Mix(s.Winner ?? -1);
        foreach (var p in s.Players)
        {
            Mix(p.PendingRefreshPoints);
            Mix(p.HasLost ? 1 : 0);
            foreach (var zone in new[] { p.Deck, p.Hand, p.Clock, p.Level, p.Stock, p.WaitingRoom, p.Memory, p.ClimaxArea, p.Resolution })
            {
                Mix(zone.Count);
                foreach (var c in zone) Mix(c.InstanceId);
            }
            foreach (var c in p.Stage)
            {
                if (c == null) { Mix(-1); continue; }
                Mix(c.InstanceId);
                Mix((int)c.Orientation);
                Mix(c.PowerModifierUntilEndOfTurn);
                Mix(c.SoulModifierUntilEndOfTurn);
            }
        }
        if (rng != null)
        {
            var (a, b, c2, d) = rng.State;
            Mix((long)a); Mix((long)b); Mix((long)c2); Mix((long)d);
        }
        return h;
    }
}
