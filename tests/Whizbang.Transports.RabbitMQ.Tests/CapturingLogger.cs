// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// ILogger&lt;T&gt; test double that records every entry and optionally raises a callback per
/// entry — used as a deterministic completion signal for log-only code paths (no Task.Delay).
/// IsEnabled always returns true so Debug-guarded branches execute.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T> {
  private readonly Lock _sync = new();
  private readonly List<(LogLevel Level, string Message)> _entries = [];

  /// <summary>Invoked synchronously after each entry is recorded.</summary>
  public Action<LogLevel, string>? OnLog { get; set; }

  public IReadOnlyList<(LogLevel Level, string Message)> Entries {
    get {
      lock (_sync) {
        return [.. _entries];
      }
    }
  }

  public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    lock (_sync) {
      _entries.Add((logLevel, message));
    }
    OnLog?.Invoke(logLevel, message);
  }
}

internal sealed class NullScope : IDisposable {
  public static readonly NullScope Instance = new();
  public void Dispose() { }
}
