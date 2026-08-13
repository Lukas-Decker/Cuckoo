using System.IO;
using System.Text;
using System.Threading.Channels;
using Cuckoo.Core;

namespace Cuckoo.Services;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>How much detail the debug.log sink records.</summary>
public enum LogVerbosity
{
    /// <summary>debug.log is not written at all.</summary>
    Off = 0,
    /// <summary>Debug detail without high-frequency trace lines (default).</summary>
    Normal = 1,
    /// <summary>Everything, including per-request and per-message trace lines.</summary>
    Verbose = 2,
}

/// <summary>
/// Application-wide file logger. Writes three sinks under logs/:
///   debug.log - everything (Debug and up)
///   info.log  - Info and up
///   error.log - Warning and Error only
/// Writes go through a background channel so logging never blocks callers,
/// and the logger itself never throws into the application.
/// </summary>
public sealed class Logger : IDisposable
{
    private const long MaxFileBytes = 5 * 1024 * 1024; // rotate at 5 MB

    public static Logger Instance { get; } = new();

    /// <summary>Debug-log verbosity; can be changed at runtime from the settings.</summary>
    public volatile LogVerbosity Verbosity = LogVerbosity.Normal;

    private readonly record struct Entry(DateTime Timestamp, LogLevel Level, int ThreadId, string Message);

    private readonly Channel<Entry> _queue;
    private readonly Task _writerTask;
    private readonly string _logsDir;
    private readonly string _debugPath;
    private readonly string _infoPath;
    private readonly string _errorPath;

    private Logger()
    {
        _logsDir = Path.Combine(Constants.WorkingDir, "logs");
        _debugPath = Path.Combine(_logsDir, "debug.log");
        _infoPath = Path.Combine(_logsDir, "info.log");
        _errorPath = Path.Combine(_logsDir, "error.log");
        _queue = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        try
        {
            Directory.CreateDirectory(_logsDir);
        }
        catch (Exception) { /* logging must never take the app down */ }
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public void Trace(string message) => Enqueue(LogLevel.Trace, message);
    public void Debug(string message) => Enqueue(LogLevel.Debug, message);
    public void Info(string message) => Enqueue(LogLevel.Info, message);
    public void Warning(string message) => Enqueue(LogLevel.Warning, message);
    public void Error(string message) => Enqueue(LogLevel.Error, message);

    public void Exception(string context, Exception exception)
        => Enqueue(LogLevel.Error, $"{context}: {exception}");

    private void Enqueue(LogLevel level, string message)
    {
        // verbosity gate: Trace only in Verbose mode; Debug unless Off
        if (level == LogLevel.Trace && Verbosity != LogVerbosity.Verbose)
            return;
        if (level == LogLevel.Debug && Verbosity == LogVerbosity.Off)
            return;
        var entry = new Entry(DateTime.Now, level, Environment.CurrentManagedThreadId, message);
        System.Diagnostics.Debug.WriteLine(Format(entry));
        _queue.Writer.TryWrite(entry);
    }

    private static string Format(in Entry entry)
        => $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level,-7}] [T{entry.ThreadId:D3}] {entry.Message}";

    private async Task WriteLoopAsync()
    {
        var debugBatch = new StringBuilder();
        var infoBatch = new StringBuilder();
        var errorBatch = new StringBuilder();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            debugBatch.Clear();
            infoBatch.Clear();
            errorBatch.Clear();
            while (_queue.Reader.TryRead(out Entry entry))
            {
                string line = Format(entry);
                debugBatch.AppendLine(line);
                if (entry.Level >= LogLevel.Info)
                    infoBatch.AppendLine(line);
                if (entry.Level >= LogLevel.Warning)
                    errorBatch.AppendLine(line);
            }
            if (Verbosity != LogVerbosity.Off)
                AppendSafe(_debugPath, debugBatch);
            AppendSafe(_infoPath, infoBatch);
            AppendSafe(_errorPath, errorBatch);
        }
    }

    private static void AppendSafe(string path, StringBuilder batch)
    {
        if (batch.Length == 0)
            return;
        try
        {
            RotateIfNeeded(path);
            File.AppendAllText(path, batch.ToString());
        }
        catch (Exception)
        {
            // disk full, locked file, etc. - drop the batch rather than crash
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.Length > MaxFileBytes)
            file.MoveTo(path + ".old", overwrite: true);
    }

    /// <summary>Flushes pending entries (bounded wait) and stops the writer.</summary>
    public void Dispose()
    {
        try
        {
            _queue.Writer.TryComplete();
            _writerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception) { }
    }
}
