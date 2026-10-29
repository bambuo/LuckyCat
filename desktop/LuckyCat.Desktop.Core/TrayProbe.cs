namespace LuckyCat.Desktop.Core;

public static class TrayProbe
{
    public static bool ShouldUseTray(bool isLinux, bool statusNotifierAvailable) =>
        !isLinux || statusNotifierAvailable;
}
