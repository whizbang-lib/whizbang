// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Captures what <see cref="StartupReadyService"/> narrates, and signals the moment a line containing
/// a given fragment is logged, so a test waits on the narration itself rather than polling for it.
/// </summary>
internal sealed class ReadyServiceLog : ILogger<StartupReadyService> {
  private readonly List<string> _entries = [];
  private readonly List<(string Fragment, TaskCompletionSource Logged)> _waiters = [];
  private readonly Lock _lock = new();

  public IReadOnlyList<string> Entries {
    get {
      lock (_lock) {
        return [.. _entries];
      }
    }
  }

  /// <summary>Completes once a line containing <paramref name="fragment"/> has been logged.</summary>
  public Task Logged(string fragment) {
    lock (_lock) {
      if (_entries.Any(e => e.Contains(fragment, StringComparison.Ordinal))) {
        return Task.CompletedTask;
      }
      var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiters.Add((fragment, logged));
      return logged.Task;
    }
  }

  /// <summary>The first line logged that contains <paramref name="fragment"/>.</summary>
  public string First(string fragment) {
    lock (_lock) {
      return _entries.First(e => e.Contains(fragment, StringComparison.Ordinal));
    }
  }

  IDisposable? ILogger.BeginScope<TState>(TState state) => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    var entry = formatter(state, exception);
    List<TaskCompletionSource> met;
    lock (_lock) {
      _entries.Add(entry);
      met = [.. _waiters.Where(w => entry.Contains(w.Fragment, StringComparison.Ordinal)).Select(w => w.Logged)];
      _ = _waiters.RemoveAll(w => met.Contains(w.Logged));
    }
    foreach (var logged in met) {
      logged.TrySetResult();
    }
  }
}
