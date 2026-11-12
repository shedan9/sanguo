// PCG XSH RR port; Copyright 2014 Melissa O'Neill. Apache-2.0.
// Upstream and license: third-party/PCG-NOTICE.md.
namespace Sanguo.BattleCore.Randomness;

public sealed record RandomState(string Algorithm, ulong State, ulong Increment, ulong DrawCount);

/// <summary>固定整数算法。初始化两次推进不计入业务消费次数。</summary>
public sealed class Pcg32
{
    public const string Algorithm = "pcg32-xsh-rr-v1";
    private ulong _state;
    private readonly ulong _increment;
    private ulong _drawCount;

    public Pcg32(ulong seed, ulong stream)
    {
        _increment = unchecked((stream << 1) | 1UL);
        NextUInt32();
        _state = unchecked(_state + seed);
        NextUInt32();
        _drawCount = 0;
    }

    public Pcg32(RandomState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Algorithm != Algorithm || (state.Increment & 1) == 0)
            throw new ArgumentException("随机流版本无效或增量不是奇数。", nameof(state));
        _state = state.State;
        _increment = state.Increment;
        _drawCount = state.DrawCount;
    }

    public RandomState Capture() => new(Algorithm, _state, _increment, _drawCount);

    public uint NextUInt32()
    {
        var old = _state;
        _state = unchecked(old * 6364136223846793005UL + _increment);
        var shifted = (uint)(((old >> 18) ^ old) >> 27);
        var rotation = (int)(old >> 59);
        _drawCount = checked(_drawCount + 1);
        return (shifted >> rotation) | (shifted << ((-rotation) & 31));
    }

    public int NextInt(int exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveUpperBound);
        var bound = (uint)exclusiveUpperBound;
        var threshold = unchecked(0u - bound) % bound;
        uint value;
        do { value = NextUInt32(); } while (value < threshold);
        return (int)(value % bound);
    }

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
