using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for tracing spans created by <see cref="PerspectiveSyncAwaiter"/>.
/// Validates that sync operations create properly named spans with correct tags
/// to show blocking time in distributed traces.
/// </summary>
/// <remarks>
/// These tests use <c>[NotInParallel]</c> because the
/// <see cref="ActivityListener"/> is global and captures spans
/// from all concurrent activity sources.
/// </remarks>
/// <docs>observability/tracing#perspective-sync</docs>
[NotInParallel(Order = 2)]
public class PerspectiveSyncTracingTests {
  // Dummy perspective type for testing
  private sealed class TestPerspective { }

  // ==========================================================================
  // WaitAsync tracing tests
  // ==========================================================================

  [Test]
  public async Task WaitAsync_CreatesSpanWithPerspectiveNameAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithEmptyTracker();
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromSeconds(1)).Build();

    // Act
    await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - span should be named with perspective type
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Name).IsEqualTo("PerspectiveSync TestPerspective");
  }

  [Test]
  public async Task WaitAsync_SetsTimeoutTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithEmptyTracker();
    var timeout = TimeSpan.FromSeconds(5);
    var options = SyncFilter.All().WithTimeout(timeout).Build();

    // Act
    await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - timeout tag should be set
    var span = collector.Spans[0];
    var timeoutTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.timeout_ms");
    await Assert.That(timeoutTag.Value).IsEqualTo(5000d);
  }

  [Test]
  public async Task WaitAsync_WithNoPendingEvents_SetsOutcomeTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithEmptyTracker();
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromSeconds(1)).Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - outcome should be NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    var span = collector.Spans[0];
    var outcomeTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.outcome");
    await Assert.That(outcomeTag.Value).IsEqualTo("NoPendingEvents");
  }

  [Test]
  public async Task WaitAsync_WithPendingEvents_SetsSyncedOutcomeOnSuccessAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Mock coordinator that returns fully synced immediately
    var coordinator = new MockWorkCoordinator((request, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = request.PerspectiveSyncInquiries?.FirstOrDefault()?.InquiryId ?? Guid.NewGuid(),
          StreamId = streamId,
          PendingCount = 0,
          ProcessedCount = 1,
          ProcessedEventIds = [eventId]
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromSeconds(5)).Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    var span = collector.Spans[0];
    var outcomeTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.outcome");
    await Assert.That(outcomeTag.Value).IsEqualTo("Synced");
  }

  [Test]
  public async Task WaitAsync_SetsEventCountTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());

    // Mock coordinator that returns fully synced
    var coordinator = new MockWorkCoordinator((request, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = request.PerspectiveSyncInquiries?.FirstOrDefault()?.InquiryId ?? Guid.NewGuid(),
          StreamId = streamId,
          PendingCount = 0,
          ProcessedCount = 3
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromSeconds(5)).Build();

    // Act
    await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - event count should be 3
    var span = collector.Spans[0];
    var eventCountTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.event_count");
    await Assert.That(eventCountTag.Value).IsEqualTo(3);
  }

  [Test]
  public async Task WaitAsync_SetsElapsedMsTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithEmptyTracker();
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromSeconds(1)).Build();

    // Act
    await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - elapsed_ms should NOT be set for NoPendingEvents (no actual waiting)
    // Only set when there's actual elapsed time from polling
    var span = collector.Spans[0];
    var elapsedTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.elapsed_ms");
    // For NoPendingEvents, elapsed is not set (immediate return)
    await Assert.That(elapsedTag.Value).IsNull();
  }

  // ==========================================================================
  // WaitForStreamAsync tracing tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_CreatesSpanWithStreamSuffixAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithSyncTracker();
    var streamId = Guid.NewGuid();

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(TestPerspective),
      streamId,
      eventTypes: null,
      timeout: TimeSpan.FromSeconds(1));

    // Assert - span should include "Stream" suffix
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Name).IsEqualTo("PerspectiveSync TestPerspective Stream");
  }

  [Test]
  public async Task WaitForStreamAsync_SetsStreamIdTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithSyncTracker();
    var streamId = Guid.NewGuid();

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(TestPerspective),
      streamId,
      eventTypes: null,
      timeout: TimeSpan.FromSeconds(1));

    // Assert - stream_id tag should be set
    var span = collector.Spans[0];
    var streamIdTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.stream_id");
    await Assert.That(streamIdTag.Value).IsEqualTo(streamId.ToString());
  }

  [Test]
  public async Task WaitForStreamAsync_WithEventIdToAwait_SetsEventIdTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithSyncTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(TestPerspective),
      streamId,
      eventTypes: null,
      timeout: TimeSpan.FromSeconds(1),
      eventIdToAwait: eventId);

    // Assert - event_id tag should be set
    var span = collector.Spans[0];
    var eventIdTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.event_id");
    await Assert.That(eventIdTag.Value).IsEqualTo(eventId.ToString());
  }

  [Test]
  public async Task WaitForStreamAsync_WithoutEventIdToAwait_DoesNotSetEventIdTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithSyncTracker();
    var streamId = Guid.NewGuid();

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(TestPerspective),
      streamId,
      eventTypes: null,
      timeout: TimeSpan.FromSeconds(1),
      eventIdToAwait: null);

    // Assert - event_id tag should NOT be set
    var span = collector.Spans[0];
    var eventIdTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.event_id");
    await Assert.That(eventIdTag.Value).IsNull();
  }

  [Test]
  public async Task WaitForStreamAsync_SetsPerspectiveTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var awaiter = _createAwaiterWithSyncTracker();
    var streamId = Guid.NewGuid();

    // Act
    await awaiter.WaitForStreamAsync(
      typeof(TestPerspective),
      streamId,
      eventTypes: null,
      timeout: TimeSpan.FromSeconds(1));

    // Assert - perspective tag should have full type name
    var span = collector.Spans[0];
    var perspectiveTag = span.Tags.FirstOrDefault(t => t.Key == "whizbang.sync.perspective");
    await Assert.That(perspectiveTag.Value?.ToString()).Contains("TestPerspective");
  }

  // ==========================================================================
  // Helpers
  // ==========================================================================

  private static PerspectiveSyncAwaiter _createAwaiterWithEmptyTracker() {
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    return new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
  }

  private static PerspectiveSyncAwaiter _createAwaiterWithSyncTracker() {
    var syncTracker = new SyncEventTracker();
    var coordinator = new MockWorkCoordinator((request, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = request.PerspectiveSyncInquiries?.FirstOrDefault()?.InquiryId ?? Guid.NewGuid(),
          PendingCount = 0,
          ProcessedCount = 0
        }
      ]
    }));
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    return new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker: syncTracker);
  }

  // Mock work coordinator for testing
  private sealed class MockWorkCoordinator : IWorkCoordinator {
    private readonly Func<ProcessWorkBatchRequest, CancellationToken, Task<WorkBatch>>? _processHandler;

    public MockWorkCoordinator() { }

    public MockWorkCoordinator(Func<ProcessWorkBatchRequest, CancellationToken, Task<WorkBatch>> processHandler) {
      _processHandler = processHandler;
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) {
      if (_processHandler is not null) {
        return _processHandler(request, ct);
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCheckpointCompletion completion, CancellationToken ct = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCheckpointFailure failure, CancellationToken ct = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }
}
