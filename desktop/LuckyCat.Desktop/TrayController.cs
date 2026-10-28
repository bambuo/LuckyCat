using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform;

namespace LuckyCat.Desktop;

public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly TrayIcons _icons;

    private TrayController(TrayIcon tray, TrayIcons icons)
    {
        _tray = tray;
        _icons = icons;
    }

    public static TrayController? Create(
        Window owner, Uri address, Action openDataFolder, Action checkUpdate, Action exit)
    {
        if (Application.Current is not { } app) return null;

        try
        {
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LuckyCat.Desktop/Assets/app.png"))),
                ToolTipText = "招财猫",
                Menu = new NativeMenu
                {
                    Items =
                    {
                        MenuItem("打开主窗口", () => ShowOwner(owner)),
                        MenuItem("复制服务地址", () => CopyAddress(owner, address)),
                        MenuItem("打开数据目录", openDataFolder),
                        MenuItem("检查更新", checkUpdate),
                        new NativeMenuItemSeparator(),
                        MenuItem("退出", exit),
                    },
                },
            };
            tray.Clicked += (_, _) => ShowOwner(owner);

            var icons = new TrayIcons { tray };
            TrayIcon.SetIcons(app, icons);
            return new TrayController(tray, icons);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException
                                       or IOException)
        {
            Console.Error.WriteLine($"创建托盘失败：{ex}");
            return null;
        }
    }

    public void Dispose()
    {
        _icons.Remove(_tray);
        _tray.Dispose();
    }

    private static NativeMenuItem MenuItem(string header, Action action) =>
        new(header) { Command = new RelayCommand(action) };

    private static void ShowOwner(Window owner)
    {
        owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate();
    }

    private static async void CopyAddress(Window owner, Uri address)
    {
        try
        {
            // 菜单命令在 UI 线程执行，直接取剪贴板即可。
            if (TopLevel.GetTopLevel(owner)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(address.ToString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"复制服务地址失败：{ex}");
        }
    }
}

internal sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action();
}
