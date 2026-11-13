using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland.UI;

/// <summary>Маленький эквалайзер: 8 полосок, анимируемых лёгким таймером (без storyboard и лишних аллокаций).</summary>
public sealed class EqualizerAnimator
{
    private readonly ScaleTransform[] _bars;
    private readonly DispatcherTimer _timer;
    private double _phase;

    public EqualizerAnimator(params ScaleTransform[] bars)
    {
        _bars = bars;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(70),
        };
        _timer.Tick += (_, _) => Step();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop()
    {
        if (_timer.IsEnabled) _timer.Stop();
        Reset();
    }

    public void Reset()
    {
        foreach (var bar in _bars) bar.ScaleY = 0.3;
    }

    private void Step()
    {
        _phase += 0.17;
        for (var i = 0; i < _bars.Length; i++)
        {
            var speed = 1.35 + i * 0.11;
            var offset = i * 1.7;
            var wave = 0.5 + 0.5 * Math.Sin(_phase * speed + offset);
            var second = 0.5 + 0.5 * Math.Sin(_phase * 2.3 + offset * 0.6);
            var level = 0.18 + 0.82 * (wave * 0.7 + second * 0.3);
            _bars[i].ScaleY = Math.Clamp(level, 0.16, 1.0);
        }
    }

    public void Dispose() => _timer.Stop();
}
