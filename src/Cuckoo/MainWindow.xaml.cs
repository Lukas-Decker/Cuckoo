using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Cuckoo.Services;
using Cuckoo.ViewModels;

namespace Cuckoo;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private MiniWindow? _miniWindow;
    private bool _reallyClosing;

    /// <summary>True once the graceful shutdown has been initiated.</summary>
    internal bool IsShuttingDown => _reallyClosing || ViewModel.CloseRequested;

    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.TrayNotification += (message, title) =>
            TrayIcon.ShowNotification(title, message);
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateTrayIcon(viewModel.TrayIconState);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // dark title bar on Windows 10 / Server 2022 (Fluent only handles this on Win11)
        DarkTitleBar.Attach(this);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TrayIconState))
            UpdateTrayIcon(ViewModel.TrayIconState);
    }

    private void UpdateTrayIcon(string state)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{state}.ico");
        TrayIcon.IconSource = new BitmapImage(uri);
        Icon = TrayIcon.IconSource;
    }

    public void MinimizeToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Switches to the compact mini-mode widget.</summary>
    public void SwitchToMini()
    {
        ViewModel.Settings.MiniMode = true;
        ViewModel.Settings.Alter();
        ViewModel.Settings.Save();
        _miniWindow ??= new MiniWindow(this);
        Hide();
        ShowInTaskbar = false;
        _miniWindow.Show();
        _miniWindow.Activate();
    }

    /// <summary>Switches back to the full GUI.</summary>
    public void SwitchToMain()
    {
        ViewModel.Settings.MiniMode = false;
        ViewModel.Settings.Alter();
        ViewModel.Settings.Save();
        _miniWindow?.Hide();
        RestoreFromTray();
    }

    /// <summary>Restores whichever view mode is active (used by the tray icon).</summary>
    private void RestoreActive()
    {
        if (ViewModel.Settings.MiniMode)
            SwitchToMini();
        else
            RestoreFromTray();
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        => RestoreActive();

    private void TrayRestore_Click(object sender, RoutedEventArgs e)
        => RestoreActive();

    private void TrayToggleMini_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Settings.MiniMode)
            SwitchToMain();
        else
            SwitchToMini();
    }

    private void MiniMode_Click(object sender, RoutedEventArgs e)
        => SwitchToMini();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
        => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // minimizing sends the window to the tray
        if (WindowState == WindowState.Minimized)
            MinimizeToTray();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            // start a graceful async shutdown instead of closing immediately
            e.Cancel = true;
            _ = ShutdownGracefullyAsync();
        }
        base.OnClosing(e);
    }

    private async Task ShutdownGracefullyAsync()
    {
        var viewModel = ViewModel;
        viewModel.CloseRequested = true;
        viewModel.SetStatus("Exiting...");
        try
        {
            viewModel.Client.Close();
            await ((App)Application.Current).WaitForClientShutdownAsync();
        }
        finally
        {
            TrayIcon.Dispose();
            _reallyClosing = true;
            Close();
            Application.Current.Shutdown();
        }
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedChannel is not null)
            ViewModel.SwitchToSelectedCommand.Execute(null);
    }

    private void OutputBox_TextChanged(object sender, TextChangedEventArgs e)
        => OutputBox.ScrollToEnd();
}
