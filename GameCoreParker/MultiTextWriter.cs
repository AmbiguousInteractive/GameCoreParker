namespace GameOptimizer;

public class MultiTextWriter : TextWriter
{
    private readonly TextWriter _baseWriter;
    private readonly StreamWriter _fileWriter;
    private readonly object _logLock = new();

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public MultiTextWriter(TextWriter baseWriter, string logPath)
    {
        _baseWriter = baseWriter;
        // Open for appending, allow other apps to read the log while we write
        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _fileWriter = new StreamWriter(stream) { AutoFlush = true };
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