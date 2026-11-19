// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Records every log entry and completes <see cref="DeadLettered"/> when the dead-letter record (EventId 1901,
/// issue #1901) is written, so a test that drives a worker waits on that exact record rather than on a delay.
/// </summary>
/// <typeparam name="T">The logger's category.</typeparam>
internal sealed class DeadLetterRecordLogger<T> : ILogger<T> {
  /// <summary>Every entry, in the order it was logged.</summary>
  public ConcurrentQueue<(LogLevel Level, int EventId, string? EventName, string Message, Exception? Exception)> Entries { get; } = new();

  /// <summary>Completes with the first dead-letter record.</summary>
  public TaskCompletionSource<(LogLevel Level, string Message, Exception? Exception)> DeadLettered { get; } =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>The dead-letter records logged so far.</summary>
  public IReadOnlyList<(LogLevel Level, int EventId, string? EventName, string Message, Exception? Exception)> Records =>
    [.. Entries.Where(e => e.EventId == DeadLetterRecordEventId.VALUE)];

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    Entries.Enqueue((logLevel, eventId.Id, eventId.Name, message, exception));
    if (eventId.Id == DeadLetterRecordEventId.VALUE) {
      DeadLettered.TrySetResult((logLevel, message, exception));
    }
  }
}

/// <summary>The dead-letter record's EventId (issue #1901), the one every dead-letter path logs.</summary>
internal static class DeadLetterRecordEventId {
  /// <summary>The EventId.</summary>
  public const int VALUE = 1901;
}
