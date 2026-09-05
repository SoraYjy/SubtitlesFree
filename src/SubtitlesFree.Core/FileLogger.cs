namespace SubtitlesFree.Core;

/// <summary>%AppData%\SubtitlesFree\log.txt 追加式日志。</summary>
public sealed class FileLogger(string dir)
{
    private readonly string _file = Path.Combine(dir, "log.txt");
    private readonly object _lock = new();

    public void Info(string msg) => Write("INFO ", msg);
    public void Warn(string msg) => Write("WARN ", msg);
    public void Error(string msg) => Write("ERROR", msg);

    private void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}\n");
            }
        }
        catch
        {
            // 日志失败不致命
        }
    }
}
