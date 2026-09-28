using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Keeps every entry, and signals the moment one arrives that a test is waiting for.
/// </summary>
/// <remarks>
/// <para>
/// The signal is what makes these tests deterministic. A schema phase spends real time on database
/// round-trips, and a test that spins until it sees a log line, rather than being told, can hold
/// the thread pool for the whole spin: the round-trip's continuation never runs, the phase makes
/// no progress at all, and the test concludes it waited long enough. That reads as a failure in a
/// couple of hundred milliseconds and reproduces only under load, which is what it did.
/// </para>
/// <para>
/// Driving a <c>FakeTimeProvider</c> from such a spin makes it worse, because the loop creates
/// the phase's whole budget in fake time while the phase is still on its first round-trip, so the
/// phase gives up having never seen the lock released. These tests use the real clock: the budget
/// is thirty seconds and the phase retries every quarter second, so releasing the lock decides
/// the outcome and scheduling cannot.
/// </para>
/// <para>
/// <see cref="Entries"/> hands out a snapshot, because the phase logs from its own thread while a
/// test reads.
/// </para>
/// </remarks>
internal sealed class SignalingListLogger : ILogger {
  private readonly List<(LogLevel Level, string Message)> _entries = [];
  private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

  /// <summary>Every entry logged so far.</summary>
  public IReadOnlyList<(LogLevel Level, string Message)> Entries {
    get {
      lock (_entries) {
        return [.. _entries];
      }
    }
  }

  /// <summary>Completes once an entry whose message contains <paramref name="fragment"/> arrives.</summary>
  /// <param name="fragment">The text to wait for.</param>
  /// <returns>A task that completes on the matching entry, or at once if one is already there.</returns>
  public Task WaitForAsync(string fragment) {
    lock (_entries) {
      if (_entries.Exists(e => e.Message.Contains(fragment, StringComparison.Ordinal))) {
        return Task.CompletedTask;
      }

      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiters.Add((fragment, signal));
      return signal.Task;
    }
  }

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(
      LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    var ready = new List<TaskCompletionSource>();

    lock (_entries) {
      _entries.Add((logLevel, message));
      for (var i = _waiters.Count - 1; i >= 0; i--) {
        if (message.Contains(_waiters[i].Fragment, StringComparison.Ordinal)) {
          ready.Add(_waiters[i].Signal);
          _waiters.RemoveAt(i);
        }
      }
    }

    // Outside the lock: a continuation the phase runs must not be able to re-enter it.
    foreach (var signal in ready) {
      signal.SetResult();
    }
  }
}
