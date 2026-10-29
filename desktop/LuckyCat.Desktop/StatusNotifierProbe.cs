using System.Diagnostics;

namespace LuckyCat.Desktop;

public static class StatusNotifierProbe
{
    public static bool Available()
    {
        try
        {
            var startInfo = new ProcessStartInfo("dbus-send")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add("--dest=org.freedesktop.DBus");
            startInfo.ArgumentList.Add("--print-reply");
            startInfo.ArgumentList.Add("--type=method_call");
            startInfo.ArgumentList.Add("/org/freedesktop/DBus");
            startInfo.ArgumentList.Add("org.freedesktop.DBus.NameHasOwner");
            startInfo.ArgumentList.Add("string:org.kde.StatusNotifierWatcher");

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            // 先异步读再等待退出：ReadToEnd 同步执行会让超时失效，dbus-send 卡住时拖死调用方。
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // 进程恰在此刻退出。
                }
                return false;
            }

            return output.GetAwaiter().GetResult().Contains("boolean true", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
