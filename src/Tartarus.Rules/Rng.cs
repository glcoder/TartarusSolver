using System.Runtime.CompilerServices;

namespace Tartarus.Rules;

/// <summary>
/// The rules' dice: a 64-bit xorshift* stream kept inside the state, so a copied state carries on the same run and
/// a seed replays it. Not the game's RNG (that can't be known); only fast and well spread.
/// </summary>
public struct Rng
{
    private ulong _s;

    public Rng(ulong seed) => _s = Mix(seed);

    /// <summary>SplitMix64's finalizer: spreads any seed (0, 1, 2 ...) over the whole state.</summary>
    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0 ? 0x2545F4914F6CDD1DUL : z;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Next()
    {
        ulong x = _s;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _s = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in [0, n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Below(int n) => (int)(((Next() >> 32) * (ulong)n) >> 32);

    /// <summary>True with <paramref name="percent"/> % odds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Chance(double percent) => NextDouble() * 100 < percent;

    /// <summary>An index drawn by weight; -1 when no weight is above 0.</summary>
    public int Pick(ReadOnlySpan<double> weights)
    {
        double total = 0;
        foreach (var w in weights) if (w > 0) total += w;
        if (total <= 0) return -1;
        double r = NextDouble() * total;
        int last = -1;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] <= 0) continue;
            last = i;
            r -= weights[i];
            if (r < 0) return i;
        }
        return last;
    }

    /// <summary>Shuffles in place (Fisher-Yates).</summary>
    public void Shuffle<T>(Span<T> items)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = Below(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
