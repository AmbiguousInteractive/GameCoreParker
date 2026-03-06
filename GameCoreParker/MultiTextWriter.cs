using System.Text;

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

    // ADD THIS: Capture grid data that doesn't use NewLines
    public override void Write(string? value)
    {
        lock (_logLock)
        {
            _baseWriter.Write(value);
            _fileWriter.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_logLock)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _baseWriter.WriteLine(value);
            _fileWriter.WriteLine($"[{timestamp}] {value}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fileWriter.Dispose();
        base.Dispose(disposing);
    }
}