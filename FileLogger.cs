using System.Text;

namespace FlowLauncher;

public class FileLogger : IDisposable
{
    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileLogger(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(path, append: true, encoding: Encoding.UTF8);
        _writer.AutoFlush = true;
    }

    public void Write(string level, string message)
    {
        _lock.Wait();
        try
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _lock.Dispose();
    }
}
