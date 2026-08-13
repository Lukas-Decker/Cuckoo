using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cuckoo.Core;

public static partial class Utils
{
    public const string CharsAscii = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const string CharsHexLower = "0123456789abcdef";

    public static string CreateNonce(string chars, int length)
        => string.Create(length, chars, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = source[Random.Shared.Next(source.Length)];
        });

    public static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int chunkLength)
    {
        var list = source.ToList();
        for (int i = 0; i < list.Count; i += chunkLength)
            yield return list.GetRange(i, Math.Min(chunkLength, list.Count - i));
    }

    /// <summary>Parses Twitch's ISO-8601 UTC timestamps ("...Z", with or without fraction).</summary>
    public static DateTime ParseTimestamp(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static string IsoNow()
        => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static readonly JsonSerializerOptions MinifiedJson = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// fnmatch-style pattern check (case-sensitive), used by the priority/exclude lists.
    /// Entries containing '*', '?' or '[' are treated as glob patterns, otherwise as literals.
    /// </summary>
    public static bool IsPatternEntry(string entry)
        => entry.Contains('*') || entry.Contains('?') || entry.Contains('[');

    public static bool MatchEntry(string entry, string name)
    {
        if (!IsPatternEntry(entry))
            return entry == name;
        return Regex.IsMatch(name, GlobToRegex(entry), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    internal static string GlobToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[':
                    int j = i + 1;
                    if (j < pattern.Length && (pattern[j] == '!' || pattern[j] == '^'))
                        j++;
                    if (j < pattern.Length && pattern[j] == ']')
                        j++;
                    while (j < pattern.Length && pattern[j] != ']')
                        j++;
                    if (j >= pattern.Length)
                    {
                        sb.Append("\\[");
                    }
                    else
                    {
                        string inner = pattern[(i + 1)..j].Replace("\\", "\\\\");
                        if (inner.StartsWith('!'))
                            inner = "^" + inner[1..];
                        sb.Append('[').Append(inner).Append(']');
                        i = j;
                    }
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}

/// <summary>Exponential backoff delay generator.</summary>
public sealed class ExponentialBackoff(double @base = 2, double variance = 0.1, double shift = 0, double maximum = 300)
{
    public int Steps { get; private set; }

    /// <summary>Returns the next delay, in seconds.</summary>
    public double Next()
    {
        double value = Math.Pow(@base, Steps)
            * (1 - variance + Random.Shared.NextDouble() * variance * 2)
            + shift;
        if (value > maximum)
            return maximum;
        Steps++;
        return value;
    }

    public void Reset() => Steps = 0;
}

/// <summary>
/// Async rate limiter: at most <paramref name="capacity"/> operations started per
/// <paramref name="window"/> seconds, and at most that many running concurrently.
/// </summary>
public sealed class RateLimiter(int capacity, double window)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<DateTime> _starts = new();
    private int _concurrent;

    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan wait = TimeSpan.Zero;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                while (_starts.Count > 0 && (now - _starts.Peek()).TotalSeconds >= window)
                    _starts.Dequeue();
                if (_starts.Count < capacity && _concurrent < capacity)
                {
                    _starts.Enqueue(now);
                    _concurrent++;
                    return new Releaser(this);
                }
                wait = _starts.Count > 0
                    ? TimeSpan.FromSeconds(window) - (now - _starts.Peek()) + TimeSpan.FromMilliseconds(10)
                    : TimeSpan.FromMilliseconds(50);
            }
            finally
            {
                _lock.Release();
            }
            if (wait < TimeSpan.FromMilliseconds(10))
                wait = TimeSpan.FromMilliseconds(10);
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void Exit() => Interlocked.Decrement(ref _concurrent);

    private sealed class Releaser(RateLimiter owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Exit();
        }
    }
}

/// <summary>A value that tasks can await until it gets set.</summary>
public sealed class AwaitableValue<T> where T : class
{
    private readonly object _sync = new();
    private TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasValue
    {
        get { lock (_sync) return _tcs.Task.IsCompletedSuccessfully; }
    }

    public Task<T> GetAsync()
    {
        lock (_sync) return _tcs.Task;
    }

    public T? GetWithDefault(T? fallback = null)
    {
        lock (_sync)
            return _tcs.Task.IsCompletedSuccessfully ? _tcs.Task.Result : fallback;
    }

    public void Set(T value)
    {
        lock (_sync)
        {
            if (_tcs.Task.IsCompleted)
                _tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs.TrySetResult(value);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_tcs.Task.IsCompleted)
                _tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

/// <summary>An awaitable, manually resettable event.</summary>
public sealed class AsyncManualResetEvent
{
    private volatile TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSet => _tcs.Task.IsCompleted;

    public Task WaitAsync() => _tcs.Task;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Set() => _tcs.TrySetResult(true);

    public void Reset()
    {
        while (true)
        {
            var tcs = _tcs;
            if (!tcs.Task.IsCompleted
                || Interlocked.CompareExchange(ref _tcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), tcs) == tcs)
                return;
        }
    }
}
