using LuckyCat.Desktop.Core;
using Xunit;

namespace LuckyCat.Tests.Desktop;

public class DesktopCoreTests
{
    [Fact]
    public void Resolve_uses_local_app_data_by_default_and_honours_override()
    {
        Assert.Equal(Path.Combine("/root/AppData", "LuckyCat"),
            DesktopPaths.Resolve(null, "/root/AppData"));
        Assert.Equal(Path.GetFullPath("/tmp/custom"),
            DesktopPaths.Resolve("/tmp/custom", "/root/AppData"));
    }

    [Fact]
    public void Match_ignores_query_and_host()
    {
        Assert.Equal("/strategy", NavigationItems.Match("/strategy?x=1")?.Path);
        Assert.Equal("/cycles", NavigationItems.Match("http://127.0.0.1:5000/cycles")?.Path);
        Assert.Null(NavigationItems.Match("/dashboard"));
    }

    [Fact]
    public void Instance_lock_is_exclusive_until_disposed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luckycat-lock-" + Guid.NewGuid().ToString("N"));
        var path = DesktopPaths.InstanceLockPath(dir);
        using var first = InstanceLock.TryAcquire(path);
        Assert.NotNull(first);
        Assert.Null(InstanceLock.TryAcquire(path));
        first!.Dispose();
        using (var second = InstanceLock.TryAcquire(path))
        {
            Assert.NotNull(second);
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Instance_file_round_trips_and_tolerates_corruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luckycat-info-" + Guid.NewGuid().ToString("N"));
        var path = DesktopPaths.InstanceInfoPath(dir);
        InstanceFile.Write(path, new InstanceInfo("http://127.0.0.1:53412", 42));
        Assert.Equal(new InstanceInfo("http://127.0.0.1:53412", 42), InstanceFile.Read(path));

        File.WriteAllText(path, "{not json");
        Assert.Null(InstanceFile.Read(path));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Log_retention_deletes_files_older_than_keep_days()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luckycat-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app-20261001.log"), "old");
        File.WriteAllText(Path.Combine(dir, "app-20261028.log"), "new");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "keep");

        var deleted = LogRetention.DeleteOlderThan(dir, new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), 7);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(dir, "app-20261001.log")));
        Assert.True(File.Exists(Path.Combine(dir, "app-20261028.log")));
        Assert.True(File.Exists(Path.Combine(dir, "other.txt")));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Tray_is_required_on_non_linux_and_optional_on_linux()
    {
        Assert.True(TrayProbe.ShouldUseTray(isLinux: false, statusNotifierAvailable: false));
        Assert.True(TrayProbe.ShouldUseTray(isLinux: true, statusNotifierAvailable: true));
        Assert.False(TrayProbe.ShouldUseTray(isLinux: true, statusNotifierAvailable: false));
    }

    [Fact]
    public void Console_redirect_writes_to_dated_file_and_restores()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luckycat-console-" + Guid.NewGuid().ToString("N"));
        var original = Console.Out;
        using (ConsoleRedirect.ToFile(dir, new DateTimeOffset(2026, 10, 29, 1, 0, 0, TimeSpan.Zero)))
        {
            Console.WriteLine("[Test] hello");
        }
        Assert.Same(original, Console.Out);
        Assert.Contains("hello", File.ReadAllText(Path.Combine(dir, "app-20261029.log")));
        Directory.Delete(dir, recursive: true);
    }
}
