using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LuckyCat.Desktop;

internal static class MacOSMenu
{
    public static void Install(Action showAbout, Action hideMainWindow, Action exit)
    {
        if (!OperatingSystem.IsMacOS()) return;

        // 应用菜单是扁平结构：NativeDock 的根菜单直接成为 macOS 应用菜单，子项不再嵌套。
        var appMenu = new NativeMenu
        {
            Items =
            {
                Item("关于招财猫", showAbout),
                new NativeMenuItemSeparator(),
                Item("隐藏招财猫", hideMainWindow, Key.W),
                Item("退出招财猫", exit, Key.Q),
            },
        };

        // 必须在 App.Initialize 内设置：Avalonia 在 OnFrameworkInitializationCompleted 之前创建应用菜单导出器。
        if (Application.Current is { } app) NativeDock.SetMenu(app, appMenu);
    }

    private static NativeMenuItem Item(string header, Action action, Key? shortcut = null) => new(header)
    {
        Command = new RelayCommand(action),
        Gesture = shortcut is { } key ? new KeyGesture(key, KeyModifiers.Meta) : null,
    };
}
