using System.Text;

namespace LuckyCat.Desktop.Core;

public static class ConsoleRedirect
{
    public static IDisposable ToFile(string logDirectory, DateTimeOffset now)
    {
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, $"app-{now:yyyyMMdd}.log");
        var writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(writer);
        Console.SetError(writer);

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
