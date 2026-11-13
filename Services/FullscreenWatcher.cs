using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Определяет, занят ли экран полноэкранным приложением, чтобы островок мог
/// улететь вверх и не мешать видео/играм.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _candidateSinceSet;
    private bool _candidate;
    private DateTime _candidateAt;
    private bool _disposed;

    public bool IsFullscreen { get; private set; }
    public event Action<bool>? Changed;

    public FullscreenWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        if (_disposed) return;
        var raw = Query();

        if (raw == _candidate)
        {
            if (_candidateSinceSet && (DateTime.UtcNow - _candidateAt).TotalMilliseconds >= 380)
            {
                _candidateSinceSet = false;
                if (IsFullscreen != raw)
                {
                    IsFullscreen = raw;
                    App.Log("fullscreen: " + raw);
                    Changed?.Invoke(raw);
                }
            }
            return;
        }

        _candidate = raw;
        _candidateAt = DateTime.UtcNow;
        _candidateSinceSet = true;
    }

    private bool Query()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            if (foreground == NativeMethods.GetShellWindow()) return false;

            NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == _ownPid) return false;

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(foreground, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SysShadow")
                return false;

            // Полноэкранные D3D-приложения (игры, видеоплееры) Windows помечает отдельно.
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
                state == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(foreground, out var window)) return false;

            // Окно во весь монитор (включая панель задач) — это фуллскрин:
            // так работают и игры, и F11 в браузере, и видеоплееры.
            // Обычное maximized-окно не подходит, оно не перекрывает панель задач.

            var monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

            var screen = info.rcMonitor;
            const int tolerance = 2;
            return window.Left <= screen.Left + tolerance &&
                   window.Top <= screen.Top + tolerance &&
                   window.Right >= screen.Right - tolerance &&
                   window.Bottom >= screen.Bottom - tolerance;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
