using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace LuckyCat.Desktop;

public partial class TitleBar : UserControl
{
    public event Action<string>? SelectionChanged;

    public TitleBar() => InitializeComponent();

    public void Select(string path)
    {
        foreach (var button in this.GetVisualDescendants().OfType<Button>()
                     .Where(b => b.Classes.Contains("nav")))
        {
            var active = string.Equals(button.Tag as string, path, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("active", active);
        }
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        Select(path);
        SelectionChanged?.Invoke(path);
    }

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;

        if (e.ClickCount == 2 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ToggleMaximize();
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window) window.WindowState = WindowState.Minimized;
    }

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as Window)?.Close();
}
