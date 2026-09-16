using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A logger that hands back a task per event id, completed when a line with that id is written.
/// </summary>
/// <remarks>
/// A worker loop that survives a failure leaves its report behind as its only trace: nothing is
/// returned, nothing is enqueued, and the loop simply goes on. Waiting on the line the loop wrote is
/// therefore the signal the loop body itself emits, which is what a worker test must wait on
/// (ai-docs/flaky-tests.md, pattern 7) — never a delay, never a spin over a snapshot.
/// </remarks>
internal sealed class EventIdSignalingLogger<TCategory> : ILogger<TCategory> {
  private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiters = new();
  private readonly ConcurrentQueue<LoggedLine> _lines = new();

  /// <summary>Everything written, in order.</summary>
  public IReadOnlyCollection<LoggedLine> Lines => _lines;

  /// <summary>Every line written under <paramref name="eventId"/>.</summary>
  public List<LoggedLine> LinesWith(int eventId) => [.. _lines.Where(l => l.EventId == eventId)];

  /// <summary>Completes once a line with <paramref name="eventId"/> has been written.</summary>
  public Task WhenLoggedAsync(int eventId, TimeSpan timeout) {
    var waiter = _waiters.GetOrAdd(eventId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    if (_lines.Any(l => l.EventId == eventId)) {
      waiter.TrySetResult();
    }
    return waiter.Task.WaitAsync(timeout);
  }

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(
      LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    ArgumentNullException.ThrowIfNull(formatter);
    _lines.Enqueue(new LoggedLine(eventId.Id, logLevel, formatter(state, exception), exception));
    if (_waiters.TryGetValue(eventId.Id, out var waiter)) {
      waiter.TrySetResult();
    }
  }

  /// <summary>One written line.</summary>
  internal sealed record LoggedLine(int EventId, LogLevel Level, string Message, Exception? Exception);
}
