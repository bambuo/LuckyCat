namespace LuckyCat.Desktop.Core;

public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock? TryAcquire(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
