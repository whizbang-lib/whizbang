// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The perspective worker describes every claimed batch at debug level, one line per (stream, perspective) group.
/// It runs on every batch, so with debug off it must cost nothing, however many groups the batch holds (#1756).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[NotInParallel("AllocationMeasurement")]
public class PerspectiveWorkerBatchCompositionLoggingTests {
  private const int ITERATIONS = 1_000;

  [Test]
  public async Task LogBatchComposition_WithDebugOff_AllocatesNothingAsync() {
    using var factory = _factory(LogLevel.Information, new CapturingLoggerProvider());
    var logger = factory.CreateLogger<PerspectiveWorker>();
    var (batch, groups) = _batchOf(groupCount: 16);
    PerspectiveWorker.LogBatchComposition(logger, batch, groups);

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      PerspectiveWorker.LogBatchComposition(logger, batch, groups);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("the batch description is debug-only; with debug off it must not box a value per group");
  }

  [Test]
  public async Task LogBatchComposition_WithDebugOn_DescribesEveryGroupAsync() {
    var capture = new CapturingLoggerProvider();
    using var factory = _factory(LogLevel.Debug, capture);
    var (batch, groups) = _batchOf(groupCount: 2);

    PerspectiveWorker.LogBatchComposition(factory.CreateLogger<PerspectiveWorker>(), batch, groups);

    await Assert.That(capture.Messages[0])
      .IsEqualTo("Perspective channel batch: PerspectiveWork count: 4, Grouped into 2 unique (StreamId, PerspectiveName) pairs");
    await Assert.That(capture.Messages[1]).IsEqualTo($"  - perspective-0/{groups[0].Key.StreamId}: 2 work items");
    await Assert.That(capture.Messages[2]).IsEqualTo($"  - perspective-1/{groups[1].Key.StreamId}: 2 work items");
    await Assert.That(capture.Messages.Count).IsEqualTo(3)
      .Because("a batch with work does not report that nothing was claimed");
  }

  [Test]
  public async Task LogBatchComposition_WithDebugOnAndNoWork_SaysNothingWasClaimedAsync() {
    var capture = new CapturingLoggerProvider();
    using var factory = _factory(LogLevel.Debug, capture);
    var (batch, groups) = _batchOf(groupCount: 0);

    PerspectiveWorker.LogBatchComposition(factory.CreateLogger<PerspectiveWorker>(), batch, groups);

    await Assert.That(capture.Messages).Contains("NO PERSPECTIVE WORK CLAIMED - check wh_message_associations and wh_perspective_cursors");
  }

  private static (WorkBatch Batch, List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> Groups) _batchOf(int groupCount) {
    var work = new List<PerspectiveWork>();
    for (var g = 0; g < groupCount; g++) {
      Guid streamId = TrackedGuid.New();
      work.Add(new PerspectiveWork { StreamId = streamId, PerspectiveName = $"perspective-{g}" });
      work.Add(new PerspectiveWork { StreamId = streamId, PerspectiveName = $"perspective-{g}" });
    }
    var batch = new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = work };
    var groups = work.GroupBy(w => (w.StreamId, w.PerspectiveName)).ToList();
    return (batch, groups);
  }

  private static ILoggerFactory _factory(LogLevel minimumLevel, CapturingLoggerProvider capture) =>
    LoggerFactory.Create(builder => {
      builder.SetMinimumLevel(minimumLevel);
      builder.AddProvider(capture);
    });

  /// <summary>Captures formatted log output; the factory's minimum level decides what reaches it.</summary>
  private sealed class CapturingLoggerProvider : ILoggerProvider {
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
    public ILogger CreateLogger(string categoryName) => new Sink(_messages);
    public void Dispose() { }

    private sealed class Sink(List<string> messages) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (messages) { messages.Add(formatter(state, exception)); }
      }
    }
  }
}
