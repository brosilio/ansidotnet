using System;

namespace AnsiDotNet.Layout;

/// <summary>
/// A float moving from one value to another over time. A plain struct: keep it in a field and call
/// <see cref="Update"/> each frame. Useful on its own for counters, colors (with <see cref="Color.Lerp"/>) or
/// anything else an app wants to animate.
/// </summary>
public struct Tween
{
    private float _from, _to, _elapsed, _duration, _delay;
    private Ease _ease;

    public Tween(float from, float to, TimeSpan duration, Ease ease = Ease.OutCubic, TimeSpan delay = default)
    {
        _from = from;
        _to = to;
        _duration = (float)Math.Max(0, duration.TotalSeconds);
        _delay = (float)Math.Max(0, delay.TotalSeconds);
        _ease = ease;
        _elapsed = 0;
        if (_duration == 0 && _delay == 0)
            _from = to;
    }

    /// <summary>A tween that is already at rest at <paramref name="value"/>.</summary>
    public static Tween At(float value) => new(value, value, TimeSpan.Zero);

    public readonly float From => _from;
    public readonly float To => _to;
    public readonly Ease Ease => _ease;
    public readonly bool IsRunning => _elapsed < _delay + _duration;

    /// <summary>Linear progress 0..1, not counting the delay.</summary>
    public readonly float Progress => _duration <= 0 ? (IsRunning ? 0 : 1) : Math.Clamp((_elapsed - _delay) / _duration, 0, 1);

    public readonly float Value
    {
        get
        {
            if (!IsRunning) return _to;
            float p = Progress;
            return _from + (_to - _from) * Easing.Apply(_ease, p);
        }
    }

    /// <summary>Advances time. Returns true while still running.</summary>
    public bool Update(TimeSpan elapsed)
    {
        if (!IsRunning)
            return false;
        _elapsed += (float)elapsed.TotalSeconds;
        return IsRunning;
    }

    /// <summary>Starts a new animation from wherever the value is now, so interrupting never jumps.</summary>
    public void Retarget(float to, TimeSpan duration, Ease ease = Ease.OutCubic, TimeSpan delay = default) =>
        this = new Tween(Value, to, duration, ease, delay);

    /// <summary>Jumps to <paramref name="value"/> and stops.</summary>
    public void Set(float value) => this = At(value);

    /// <summary>Jumps to the end.</summary>
    public void Finish() => this = At(_to);

    public static implicit operator float(Tween t) => t.Value;

    public override readonly string ToString() => IsRunning ? $"{Value:0.###} → {_to:0.###}" : $"{_to:0.###}";
}
