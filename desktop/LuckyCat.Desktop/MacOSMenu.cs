using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LuckyCat.Desktop;

internal static class MacOSMenu
{
    public static void Install(Action showAbout, Action hideMainWindow, Action exit)
    {
        if (!OperatingSystem.IsMacOS()) return;

        // 应用菜单是扁平结构：根菜单的子项直接成为 macOS 菜单栏中的应用菜单项。
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

        // 必须挂在 Application 的 NativeMenu 上：macOS 导出器读取 NativeMenu.GetMenu(Application.Current)
        // 并写入菜单栏；NativeDock 的菜单只会进入 Dock 右键菜单。
        // 必须在 App.Initialize 内设置：导出器在 AfterSetup 阶段创建时读取该菜单。
        if (Application.Current is { } app) NativeMenu.SetMenu(app, appMenu);
    }

    private static NativeMenuItem Item(string header, Action action, Key? shortcut = null) => new(header)
    {
        Command = new RelayCommand(action),
        Gesture = shortcut is { } key ? new KeyGesture(key, KeyModifiers.Meta) : null,
    };
}
