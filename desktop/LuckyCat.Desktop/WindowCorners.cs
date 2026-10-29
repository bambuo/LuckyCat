using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace LuckyCat.Desktop;

// 无边框窗口的圆角只能由平台原生层完成：Avalonia 的 Window 没有 CornerRadius，
// 自绘或透明窗口裁不住原生 WebView 并会破坏系统阴影。
internal static class WindowCorners
{
    private const double Radius = 10;

    public static void Apply(Window window) => Apply(window, allowRetry: true);

    private static void Apply(Window window, bool allowRetry)
    {
        var platform = window.TryGetPlatformHandle();
        var handle = OperatingSystem.IsMacOS()
            ? (platform as IMacOSTopLevelPlatformHandle)?.NSWindow ?? IntPtr.Zero
            : platform?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            // Opened 时句柄通常已存在；缺失时排队重试一次，避免静默放弃。
            if (allowRetry)
                Dispatcher.UIThread.Post(() => Apply(window, allowRetry: false), DispatcherPriority.Background);
            return;
        }

        if (OperatingSystem.IsMacOS()) ApplyMac(handle);
        else if (OperatingSystem.IsWindows()) ApplyWindows(handle);
        // Linux：圆角由合成器决定，不处理
    }

    private static void ApplyMac(IntPtr window)
    {
        if (!Mac.IsNsWindow(window)) return;

        // Avalonia 的无边框窗口关掉了窗口管理器阴影；不恢复，圆角窗口就没有系统投影。
        Mac.SetHasShadow(window, true);

        var contentView = Mac.ContentView(window);
        if (contentView == IntPtr.Zero) return;

        Mac.SetWantsLayer(contentView, true);
        var layer = Mac.Layer(contentView);
        if (layer == IntPtr.Zero) return;

        Mac.SetCornerRadius(layer, Radius);
        Mac.SetMasksToBounds(layer, true);
    }

    private static void ApplyWindows(IntPtr hwnd)
    {
        var preference = Windows.DwmwcpRound;
        // Win10 及更早版本没有该属性，调用失败即保持直角，无需提示。
        _ = Windows.DwmSetWindowAttribute(hwnd, Windows.DwmaWindowCornerPreference,
            ref preference, sizeof(int));
    }

    private static class Mac
    {
        private const string LibObjC = "/usr/lib/libobjc.A.dylib";

        // arm64 上 objc_msgSend 没有统一签名：按返回值（对象/BOOL/void）与参数类型分别声明。
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSendPtr(IntPtr receiver, IntPtr selector);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void MsgSendVoidBool(IntPtr receiver, IntPtr selector,
            [MarshalAs(UnmanagedType.I1)] bool value);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        private static extern void MsgSendVoidDouble(IntPtr receiver, IntPtr selector, double value);

        [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool MsgSendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr value);

        [DllImport(LibObjC)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(LibObjC)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        private static readonly IntPtr NsWindowClass = objc_getClass("NSWindow");
        private static readonly IntPtr SetHasShadowSelector = sel_registerName("setHasShadow:");
        private static readonly IntPtr SetWantsLayerSelector = sel_registerName("setWantsLayer:");
        private static readonly IntPtr SetCornerRadiusSelector = sel_registerName("setCornerRadius:");
        private static readonly IntPtr SetMasksToBoundsSelector = sel_registerName("setMasksToBounds:");
        private static readonly IntPtr IsKindOfClassSelector = sel_registerName("isKindOfClass:");
        private static readonly IntPtr ContentViewSelector = sel_registerName("contentView");
        private static readonly IntPtr LayerSelector = sel_registerName("layer");

        public static bool IsNsWindow(IntPtr handle) =>
            NsWindowClass != IntPtr.Zero && MsgSendBoolPtr(handle, IsKindOfClassSelector, NsWindowClass);

        public static IntPtr ContentView(IntPtr window) => MsgSendPtr(window, ContentViewSelector);

        public static IntPtr Layer(IntPtr view) => MsgSendPtr(view, LayerSelector);

        public static void SetHasShadow(IntPtr window, bool value) =>
            MsgSendVoidBool(window, SetHasShadowSelector, value);

        public static void SetWantsLayer(IntPtr view, bool value) =>
            MsgSendVoidBool(view, SetWantsLayerSelector, value);

        public static void SetMasksToBounds(IntPtr layer, bool value) =>
            MsgSendVoidBool(layer, SetMasksToBoundsSelector, value);

        public static void SetCornerRadius(IntPtr layer, double value) =>
            MsgSendVoidDouble(layer, SetCornerRadiusSelector, value);
    }

    private static class Windows
    {
        public const int DwmaWindowCornerPreference = 33;
        public const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
