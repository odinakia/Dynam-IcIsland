using System.IO;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using DynamicIsland.UI;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DynamicIsland;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;

    public static Settings AppSettings { get; private set; } = new();
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        _mutex = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log("UI exception: " + args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);

        AppSettings = Settings.Load();
        Log("--- start ---");
        NativeMethods.TrimWorkingSet();

        _window = new MainWindow(AppSettings);
        _window.Show();
        _window.PositionAtTopCenter();

        CreateTrayIcon();
    }

    private void CreateTrayIcon()
    {
        try
        {
            _tray = new WinForms.NotifyIcon
            {
                Text = "Dynamic Island",
                Visible = true,
                Icon = LoadTrayIcon(),
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Открыть / свернуть островок", null, (_, _) => _window?.ToggleFromTray());
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var hideFullscreen = new WinForms.ToolStripMenuItem("Скрывать в полноэкранном режиме")
            {
                Checked = AppSettings.HideInFullscreen,
                CheckOnClick = true,
            };
            hideFullscreen.CheckedChanged += (_, _) =>
            {
                AppSettings.HideInFullscreen = hideFullscreen.Checked;
                AppSettings.Save();
                _window?.ApplySettings();
            };
            menu.Items.Add(hideFullscreen);

            var autoStart = new WinForms.ToolStripMenuItem("Запускать с Windows")
            {
                Checked = IsAutoStartEnabled(),
                CheckOnClick = true,
            };
            autoStart.CheckedChanged += (_, _) => SetAutoStart(autoStart.Checked);
            menu.Items.Add(autoStart);

            menu.Items.Add("Город для погоды…", null, (_, _) => AskCity());
            menu.Items.Add("Очистить полку", null, (_, _) => _window?.ClearShelf());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Shutdown());

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => _window?.ToggleFromTray();
        }
        catch (Exception ex)
        {
            Log("tray failed: " + ex);
        }
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch (Exception ex)
        {
            Log("tray icon: " + ex.Message);
        }

        return Drawing.SystemIcons.Application;
    }

    private void AskCity()
    {
        var dialog = new CityDialog(AppSettings.City);
        if (dialog.ShowDialog() == true)
        {
            AppSettings.City = dialog.City;
            AppSettings.Save();
            _window?.ReloadWeather();
        }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("DynamicIsland") != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath != null)
                key.SetValue("DynamicIsland", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("DynamicIsland", false);
        }
        catch (Exception ex)
        {
            Log("autostart: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _window?.ShutdownServices();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _mutex?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }

    public static void Log(string message)
    {
        try
        {
            var file = Path.Combine(DataDir, "log.txt");
            if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024)
                File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break the app
        }
    }
}
