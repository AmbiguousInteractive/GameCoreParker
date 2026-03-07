using System.Text;
using GameOptimizer;

public class MultiTextWriter : TextWriter
{
    private readonly TextWriter _baseWriter;
    private readonly StreamWriter _fileWriter;
    private readonly object _logLock = new();
    public override Encoding Encoding => Encoding.UTF8;

    public MultiTextWriter(TextWriter baseWriter, string logPath)
    {
        _baseWriter = baseWriter;
        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _fileWriter = new StreamWriter(stream) { AutoFlush = true };
    }

    public override void Write(string? value)
    {
        // BLOCK: If menu is open, don't write anything to console or log
        if (Program.IsMenuOpenInternal) return;

        lock (_logLock)
        {
            _baseWriter.Write(value);
            if (value != null) _fileWriter.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        // If ANY menu/submenu is open, we discard background logs
        if (Program.IsMenuOpenInternal) return;

        lock (_logLock)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _baseWriter.WriteLine(value);
            if (_fileWriter != null) _fileWriter.WriteLine($"[{timestamp}] {value}");
        }
    }
}