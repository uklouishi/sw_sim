namespace WsSim.Core.Randomness;

/// <summary>
/// 可设定种子的确定性随机数生成器（xoshiro256**，用 SplitMix64 由种子初始化）。
/// 不使用 System.Random：它的算法可能随 .NET 版本变化，联机双方必须得到完全一样的序列。
/// 对局中的所有随机（先后手、洗牌）都只能走这个类。
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public ulong Seed { get; }

    public DeterministicRandom(ulong seed)
    {
        Seed = seed;
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    private static ulong SplitMix64(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>返回 [0, maxExclusive) 的均匀整数（拒绝采样，无偏）。</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        ulong bound = (ulong)maxExclusive;
        ulong limit = ulong.MaxValue - (ulong.MaxValue % bound);
        ulong r;
        do { r = NextUInt64(); } while (r >= limit);
        return (int)(r % bound);
    }

    /// <summary>Fisher–Yates 洗牌。</summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>当前内部状态，用于状态哈希和存档。</summary>
    public (ulong, ulong, ulong, ulong) State => (_s0, _s1, _s2, _s3);
}
