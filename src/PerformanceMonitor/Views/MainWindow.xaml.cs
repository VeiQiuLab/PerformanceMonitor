using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PerformanceMonitor.Configuration;
using PerformanceMonitor.ViewModels;

namespace PerformanceMonitor.Views;

public partial class MainWindow : Window
{
    private const int WmQueryEndSession = 0x0011;
    private readonly AppController _controller;
    private readonly MainViewModel _viewModel = new();
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _exitRequested;
    private bool _settingsOpen;
    private HwndSource? _windowSource;

    internal MainWindow(AppController controller, AppSettings settings)
    {
        InitializeComponent();
        _controller = controller;
        DataContext = _viewModel;
        _viewModel.Reconcile(settings);
        RestoreWindow(settings.Window);
        _controller.DeviceStateChanged += OnDeviceStateChanged;
        _controller.ServiceMessageChanged += OnServiceMessageChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _controller.StartAsync();
        }
        catch (Exception)
        {
            _viewModel.ServiceMessage = "Monitoring could not start. Check local permissions and settings.";
        }
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateUpdate update)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => _viewModel.Apply(update));
        }
    }

    private void OnServiceMessageChanged(object? sender, string? message)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => _viewModel.ServiceMessage = message);
        }
    }

    internal event EventHandler? HiddenToTray;

    private async void OnSettingsClick(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    internal async Task ShowSettingsAsync()
    {
        if (_settingsOpen)
        {
            return;
        }

        RestoreFromTray();
        _settingsOpen = true;
        var dialog = new SettingsWindow(_controller.CurrentSettings) { Owner = this };
        try
        {
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            IsEnabled = false;
            try
            {
                await _controller.ApplySettingsAsync(dialog.Result);
                _viewModel.Reconcile(dialog.Result);
            }
            catch (Exception)
            {
                System.Windows.MessageBox.Show(this, "Settings could not be saved or applied.", "Performance Monitor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsEnabled = true;
            }
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    internal void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    internal void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            if (_windowSource is not null)
            {
                _windowSource.RemoveHook(OnWindowMessage);
                _windowSource = null;
            }

            SourceInitialized -= OnSourceInitialized;
            _controller.DeviceStateChanged -= OnDeviceStateChanged;
            _controller.ServiceMessageChanged -= OnServiceMessageChanged;
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        var window = new WindowSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized
        };
        try
        {
            await _controller.SaveWindowSettingsAsync(window);
        }
        catch (Exception)
        {
            // Closing must remain possible even if the user profile is read-only.
        }

        if (_exitRequested)
        {
            _allowClose = true;
            Close();
            return;
        }

        Hide();
        _closeInProgress = false;
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmQueryEndSession)
        {
            _exitRequested = true;
        }

        return 0;
    }

    private void RestoreWindow(WindowSettings window)
    {
        Width = window.Width;
        Height = window.Height;
        if (double.IsFinite(window.Left) && double.IsFinite(window.Top) &&
            window.Left >= SystemParameters.VirtualScreenLeft &&
            window.Left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
            window.Top >= SystemParameters.VirtualScreenTop &&
            window.Top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = window.Left;
            Top = window.Top;
        }

        if (window.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }
}
