using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Tests.Helpers;

/// <summary>One captured log call.</summary>
internal sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// A logger that records every call so a test can assert on what the code under test said, and can
/// wait for a specific entry with a completion signal instead of polling. Shared by the worker and
/// messaging tests that assert operator-facing messages (what failed and what happens next).
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T> {
  private readonly List<CapturedLog> _entries = [];
  private readonly List<(Func<CapturedLog, bool> Predicate, TaskCompletionSource<CapturedLog> Signal)> _waiters = [];

  public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(
      LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    var entry = new CapturedLog(logLevel, formatter(state, exception), exception);
    List<TaskCompletionSource<CapturedLog>>? matched = null;
    lock (_entries) {
      _entries.Add(entry);
      for (var i = _waiters.Count - 1; i >= 0; i--) {
        if (_waiters[i].Predicate(entry)) {
          (matched ??= []).Add(_waiters[i].Signal);
          _waiters.RemoveAt(i);
        }
      }
    }
    matched?.ForEach(s => s.TrySetResult(entry));
  }

  /// <summary>A copy of everything logged so far.</summary>
  public List<CapturedLog> Snapshot() {
    lock (_entries) { return [.. _entries]; }
  }

  /// <summary>
  /// Completes with the first entry matching <paramref name="predicate"/>: immediately if one was
  /// already logged, otherwise when it arrives. A completion signal, never a poll.
  /// </summary>
  public Task<CapturedLog> WaitForAsync(Func<CapturedLog, bool> predicate) {
    lock (_entries) {
      var existing = _entries.FirstOrDefault(predicate);
      if (existing is not null) {
        return Task.FromResult(existing);
      }
      var signal = new TaskCompletionSource<CapturedLog>(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiters.Add((predicate, signal));
      return signal.Task;
    }
  }

  private sealed class NullScope : IDisposable {
    public static readonly NullScope Instance = new();
    public void Dispose() { }
  }
}
