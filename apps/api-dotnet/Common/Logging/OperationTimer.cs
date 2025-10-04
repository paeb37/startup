namespace Dexter.WebApi.Common.Logging;

using System;
using System.Diagnostics;

/// <summary>
/// Helper to measure elapsed time and write structured timing logs.
/// </summary>
internal sealed class OperationTimer : IDisposable
{
    private readonly string _operation;
    private readonly string? _detail;
    private readonly Stopwatch _stopwatch;
    private bool _disposed;

    private OperationTimer(string operation, string? detail)
    {
        _operation = operation;
        _detail = detail;
        _stopwatch = Stopwatch.StartNew();
    }

    public static OperationTimer Start(string operation, string? detail = null)
        => new(operation, detail);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stopwatch.Stop();
        LogTiming(_operation, _stopwatch.Elapsed, _detail);
        _disposed = true;
    }

    public static void LogTiming(string operation, TimeSpan elapsed, string? detail = null)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" :: {detail}";
        Console.WriteLine($"[timing] {operation}{suffix} took {elapsed.TotalMilliseconds:F0} ms");
    }
}
