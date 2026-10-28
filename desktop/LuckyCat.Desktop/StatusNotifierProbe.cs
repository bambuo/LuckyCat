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
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Contains("boolean true", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
