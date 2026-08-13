using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cuckoo.Services;
using Cuckoo.ViewModels;

namespace Cuckoo;

/// <summary>
/// Compact "mini mode" farming widget: frameless card with traffic-light dots,
/// game box art, status, current drop and a configurable progress bar.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly MainWindow _owner;
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private Settings Settings => ViewModel.Settings;

    public MiniWindow(MainWindow owner)
    {
        _owner = owner;
        DataContext = owner.DataContext;
        InitializeComponent();
        ApplyTheme();
        RestorePosition();
    }

    private void ApplyTheme()
    {
        bool dark = DarkTitleBar.IsSystemDarkMode();
        Card.Background = new SolidColorBrush(dark
            ? Color.FromArgb(0xF2, 0x26, 0x29, 0x2E)
            : Color.FromArgb(0xF6, 0xFA, 0xFA, 0xFA));
        Card.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x28, 0x00, 0x00, 0x00));
        Foreground = new SolidColorBrush(dark
            ? Color.FromRgb(0xF0, 0xF0, 0xF0)
            : Color.FromRgb(0x1B, 0x1B, 0x1B));
    }

    private void RestorePosition()
    {
        double? left = Settings.MiniLeft;
        double? top = Settings.MiniTop;
        Rect workArea = SystemParameters.WorkArea;
        if (left is not null && top is not null
            && left >= workArea.Left - 40 && left <= workArea.Right - 60
            && top >= workArea.Top - 20 && top <= workArea.Bottom - 60)
        {
            Left = left.Value;
            Top = top.Value;
        }
        else
        {
            // default: bottom-right corner of the work area
            Left = workArea.Right - Width - 24;
            Top = workArea.Bottom - 320;
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Settings.MiniLeft = Left;
        Settings.MiniTop = Top;
        Settings.Alter(); // persisted with the next save (settings change or shutdown)
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_owner.IsShuttingDown)
        {
            // Alt+F4 etc. on the widget: route through the main graceful shutdown
            e.Cancel = true;
            _owner.Close();
            return;
        }
        base.OnClosing(e);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseDot_Click(object sender, RoutedEventArgs e)
        => _owner.Close(); // graceful app shutdown

    private void MinimizeDot_Click(object sender, RoutedEventArgs e)
        => Hide(); // to tray; restore via tray icon

    private void ExpandDot_Click(object sender, RoutedEventArgs e)
        => _owner.SwitchToMain();
}
