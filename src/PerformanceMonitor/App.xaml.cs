using System.Windows;
using System.Windows.Threading;
using PerformanceMonitor.Configuration;
using PerformanceMonitor.Services;
using PerformanceMonitor.Views;

namespace PerformanceMonitor;

public partial class App : System.Windows.Application
{
    private const string ExitEventName = @"Local\PerformanceMonitor-59561B17-DAC5-4192-8B7C-3F69E0AA4B00-Exit";
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private string? _smokeSettingsPath;
    private TrayIconService? _trayIcon;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--request-running-exit", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(RequestRunningInstancesExit() ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--remove-startup-task", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                new StartupTaskService().Remove();
                Shutdown(0);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Shutdown(1);
            }

            return;
        }

        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent,
            (_, timedOut) =>
            {
                if (!timedOut && !Dispatcher.HasShutdownStarted)
                {
                    _ = Dispatcher.BeginInvoke(RequestExit);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        var smokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var startMinimized = e.Args.Contains("--start-minimized", StringComparer.OrdinalIgnoreCase);
        if (smokeTest)
        {
            _smokeSettingsPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PerformanceMonitor-smoke-{Guid.NewGuid():N}",
                "settings.json");
        }

        var store = new SettingsStore(_smokeSettingsPath);
        var settings = store.Load();
        _controller = new AppController(store, settings, manageStartupTask: !smokeTest && !startMinimized);
        _mainWindow = new MainWindow(_controller, settings);
        MainWindow = _mainWindow;
        _mainWindow.HiddenToTray += OnHiddenToTray;
        _mainWindow.Closed += OnMainWindowClosed;
        _trayIcon = new TrayIconService(ShowMainWindow, ShowSettings, RequestExit);
        _mainWindow.Show();
        if (startMinimized)
        {
            _mainWindow.Hide();
        }

        if (smokeTest)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RequestExit();
            };
            timer.Start();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is not null)
        {
            _ = _mainWindow.Dispatcher.BeginInvoke(_mainWindow.RestoreFromTray);
        }
    }

    private void ShowSettings()
    {
        if (_mainWindow is not null)
        {
            _ = _mainWindow.Dispatcher.BeginInvoke(() => _ = _mainWindow.ShowSettingsAsync());
        }
    }

    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        if (_mainWindow is null)
        {
            Shutdown();
            return;
        }

        _ = _mainWindow.Dispatcher.BeginInvoke(_mainWindow.RequestExit);
    }

    private void OnHiddenToTray(object? sender, EventArgs e) => _trayIcon?.ShowHiddenNotice();

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_exitRequested)
        {
            Shutdown();
        }
    }

    private static bool RequestRunningInstancesExit()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var targets = System.Diagnostics.Process.GetProcessesByName("PerformanceMonitor")
            .Where(process => process.Id != Environment.ProcessId && IsSameExecutable(process, executablePath))
            .ToArray();

        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
            exitEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException) when (targets.Length == 0)
        {
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        foreach (var process in targets)
        {
            using (process)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !process.WaitForExit((int)remaining.TotalMilliseconds))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsSameExecutable(System.Diagnostics.Process process, string executablePath)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            process.Dispose();
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exitWait?.Unregister(null);
        _exitWait = null;
        _exitEvent?.Dispose();
        _exitEvent = null;

        if (_mainWindow is not null)
        {
            _mainWindow.HiddenToTray -= OnHiddenToTray;
            _mainWindow.Closed -= OnMainWindowClosed;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_controller is not null)
        {
            try
            {
                _controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                e.ApplicationExitCode = 1;
            }
        }

        if (_smokeSettingsPath is not null)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_smokeSettingsPath);
                var tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
                if (directory is not null &&
                    System.IO.Path.GetFullPath(directory).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Path.GetFileName(directory).StartsWith("PerformanceMonitor-smoke-", StringComparison.Ordinal) &&
                    Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        base.OnExit(e);
    }
}
