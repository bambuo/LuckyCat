using System.Text;

namespace LuckyCat.Desktop.Core;

public static class ConsoleRedirect
{
    public static IDisposable ToFile(string logDirectory, DateTimeOffset now)
    {
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, $"app-{now:yyyyMMdd}.log");
        var writer = new StreamWriter(
            // ReadWrite 允许第二个实例同时追加同一日志（例如用户重复双击启动）。
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        // Out 与 Error 共用一个同步包装，避免多线程并发写同一 writer；只释放底层 writer。
        var sync = TextWriter.Synchronized(writer);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(sync);
        Console.SetError(sync);

        return new Restore(() =>
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            writer.Dispose();
        });
    }

    private sealed class Restore(Action action) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            action();
        }
    }
}
