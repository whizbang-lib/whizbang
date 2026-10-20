// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Component.Tests.TestLogging;

/// <summary>
/// Collects the error entries logged under one category, and signals the first. A detached stage's failure is only
/// logged, so a test waits on this signal to see it.
/// </summary>
public sealed class ErrorSignalingLoggerProvider(string category) : ILoggerProvider {
  private readonly ConcurrentQueue<(EventId EventId, string Message, Exception? Exception)> _errors = new();

  public TaskCompletionSource FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public (EventId EventId, string Message, Exception? Exception)[] Errors => [.. _errors];

  public ILogger CreateLogger(string categoryName) => categoryName == category ? new Logger(this) : NullLogger.Instance;

  public void Dispose() { }

  private sealed class Logger(ErrorSignalingLoggerProvider owner) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (!IsEnabled(logLevel)) {
        return;
      }
      owner._errors.Enqueue((eventId, formatter(state, exception), exception));
      owner.FirstError.TrySetResult();
    }
  }
}
