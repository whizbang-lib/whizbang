using Whizbang.Core.Messaging;
using Whizbang.Testing.Workers;

namespace Whizbang.Testing.Tests.Workers;

/// <summary>
/// Tests for <see cref="PerspectiveWorkerTestHarness"/> and its capturing channel doubles.
/// </summary>
public class PerspectiveWorkerTestHarnessTests {
  private static PerspectiveWork _work(Guid streamId) => new() {
    WorkId = Guid.NewGuid(),
    StreamId = streamId,
    PerspectiveName = "TestPerspective"
  };

  [Test]
  public async Task EnqueueWorkAsync_WritesToPerspectiveChannelAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    var streamId = Guid.NewGuid();

    await harness.EnqueueWorkAsync(_work(streamId));

    var read = await harness.ChannelWriter.Reader.ReadAsync();
    await Assert.That(read.StreamId).IsEqualTo(streamId);
    await Assert.That(read.PerspectiveName).IsEqualTo("TestPerspective");
  }

  [Test]
  public async Task EnqueueWorkAsync_PreservesOrderAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();

    await harness.EnqueueWorkAsync(_work(first));
    await harness.EnqueueWorkAsync(_work(second));

    await Assert.That((await harness.ChannelWriter.Reader.ReadAsync()).StreamId).IsEqualTo(first);
    await Assert.That((await harness.ChannelWriter.Reader.ReadAsync()).StreamId).IsEqualTo(second);
  }

  [Test]
  public async Task EnqueueDrainStreamAsync_WritesToDrainChannelAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    var streamId = Guid.NewGuid();

    await harness.EnqueueDrainStreamAsync(streamId);

    var read = await harness.DrainChannel.Reader.ReadAsync();
    await Assert.That(read).IsEqualTo(streamId);
  }

  [Test]
  public async Task CompletionCapture_EnqueueEventWorkId_CapturesAndSignalsFirstAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    await Assert.That(harness.CompletionCapture.FirstEventWorkId.IsCompleted).IsFalse();
    var workId = Guid.NewGuid();

    await harness.CompletionCapture.EnqueueEventWorkIdAsync(workId);

    await harness.CompletionCapture.FirstEventWorkId;
    await Assert.That(harness.CompletionCapture.EventWorkIds.Count).IsEqualTo(1);
    await Assert.That(harness.CompletionCapture.EventWorkIds.TryPeek(out var captured)).IsTrue();
    await Assert.That(captured).IsEqualTo(workId);
  }

  [Test]
  public async Task CompletionCapture_EnqueueCursor_CapturesAndSignalsFirstAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    await Assert.That(harness.CompletionCapture.FirstCursor.IsCompleted).IsFalse();
    var cursor = new PerspectiveCursorCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Processing
    };

    await harness.CompletionCapture.EnqueueCursorAsync(cursor);

    await harness.CompletionCapture.FirstCursor;
    await Assert.That(harness.CompletionCapture.Cursors.Count).IsEqualTo(1);
    await Assert.That(harness.CompletionCapture.Cursors.TryPeek(out var captured)).IsTrue();
    await Assert.That(captured).IsEqualTo(cursor);
  }

  [Test]
  public async Task FailureCapture_Enqueue_CapturesCategoryAndFailureAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    var failure = new MessageFailure {
      MessageId = Guid.NewGuid(),
      CompletedStatus = MessageProcessingStatus.Stored,
      Error = "kaboom"
    };

    await harness.FailureCapture.EnqueueAsync(WorkCategory.PerspectiveEvent, failure);

    await Assert.That(harness.FailureCapture.Items.Count).IsEqualTo(1);
    await Assert.That(harness.FailureCapture.Items.TryPeek(out var captured)).IsTrue();
    await Assert.That(captured.category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(captured.failure.Error).IsEqualTo("kaboom");
  }

  [Test]
  public async Task LeaseRenewalCapture_Enqueue_CapturesCategoryAndIdAsync() {
    var harness = new PerspectiveWorkerTestHarness();
    var leaseId = Guid.NewGuid();

    await harness.LeaseRenewalCapture.EnqueueAsync(WorkCategory.Outbox, leaseId);

    await Assert.That(harness.LeaseRenewalCapture.Items.Count).IsEqualTo(1);
    await Assert.That(harness.LeaseRenewalCapture.Items.TryPeek(out var captured)).IsTrue();
    await Assert.That(captured.category).IsEqualTo(WorkCategory.Outbox);
    await Assert.That(captured.id).IsEqualTo(leaseId);
  }

  [Test]
  public async Task CompletionCapture_MultipleEnqueues_AllCapturedAsync() {
    var harness = new PerspectiveWorkerTestHarness();

    await harness.CompletionCapture.EnqueueEventWorkIdAsync(Guid.NewGuid());
    await harness.CompletionCapture.EnqueueEventWorkIdAsync(Guid.NewGuid());
    await harness.CompletionCapture.EnqueueEventWorkIdAsync(Guid.NewGuid());

    await Assert.That(harness.CompletionCapture.EventWorkIds.Count).IsEqualTo(3);
  }
}
