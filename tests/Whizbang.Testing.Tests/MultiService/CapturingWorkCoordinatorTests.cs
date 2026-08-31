using System.Text.Json;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.MultiService;

namespace Whizbang.Testing.Tests.MultiService;

/// <summary>
/// Tests for <see cref="CapturingWorkCoordinator"/> — the coordinator double a multi-service test
/// uses to observe what a consumer actually wrote to its inbox.
/// </summary>
/// <remarks>
/// This is a test double, which is exactly why it needs covering: a bug here does not fail, it
/// makes other tests pass. If <see cref="CapturingWorkCoordinator.WaitForInboxAsync"/> returned
/// early, every test that waits on it would assert against a partial capture and go green on
/// half the messages.
///
/// <para>
/// It also has to be a real completion signal rather than a poll — that is the whole reason to
/// use it instead of a delay. So the wait must return as soon as the count is reached, whether
/// the messages arrived before the wait started or while it was already parked.
/// </para>
/// </remarks>
public class CapturingWorkCoordinatorTests {

  private static InboxMessage _message() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new InboxMessage {
      MessageId = msgId,
      StreamId = (Guid)TrackedGuid.NewMedo(),
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    };
  }

  [Test]
  public async Task StoredInboxMessages_IsEmptyBeforeAnythingIsStoredAsync() {
    var coordinator = new CapturingWorkCoordinator();

    await Assert.That(coordinator.StoredInboxMessages).IsEmpty();
  }

  [Test]
  public async Task StoreInboxMessages_CapturesEveryMessageAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var messages = new[] { _message(), _message(), _message() };

    await coordinator.StoreInboxMessagesAsync(messages);

    await Assert.That(coordinator.StoredInboxMessages.Count).IsEqualTo(3);
  }

  [Test]
  public async Task StoreInboxMessages_AccumulatesAcrossCallsAsync() {
    // A consumer writes its inbox in batches, so a double that replaced rather than appended
    // would silently drop everything but the last batch.
    var coordinator = new CapturingWorkCoordinator();

    await coordinator.StoreInboxMessagesAsync([_message()], cancellationToken: CancellationToken.None);
    await coordinator.StoreInboxMessagesAsync([_message(), _message()], cancellationToken: CancellationToken.None);

    await Assert.That(coordinator.StoredInboxMessages.Count).IsEqualTo(3);
  }

  [Test]
  public async Task StoredInboxMessages_ReturnsASnapshotAsync() {
    // Handing back the live list would let a later capture mutate a collection a test is
    // already asserting over — a race that shows up as an intermittent count mismatch.
    var coordinator = new CapturingWorkCoordinator();
    await coordinator.StoreInboxMessagesAsync([_message()], cancellationToken: CancellationToken.None);

    var snapshot = coordinator.StoredInboxMessages;
    await coordinator.StoreInboxMessagesAsync([_message()], cancellationToken: CancellationToken.None);

    await Assert.That(snapshot.Count).IsEqualTo(1)
      .Because("a snapshot taken before the second write must not grow behind the test's back");
    await Assert.That(coordinator.StoredInboxMessages.Count).IsEqualTo(2);
  }

  [Test]
  public async Task StoreInboxMessages_WithAnEmptyBatch_IsHarmlessAsync() {
    var coordinator = new CapturingWorkCoordinator();

    await coordinator.StoreInboxMessagesAsync([], cancellationToken: CancellationToken.None);

    await Assert.That(coordinator.StoredInboxMessages).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_ReturnsImmediatelyWhenTheCountIsAlreadyMetAsync(
      CancellationToken cancellationToken) {
    // The common shape: the messages landed before the assertion ran. Parking here would turn
    // every already-satisfied wait into a full timeout.
    var coordinator = new CapturingWorkCoordinator();
    await coordinator.StoreInboxMessagesAsync([_message(), _message()], cancellationToken: CancellationToken.None);

    var captured = await coordinator.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));

    await Assert.That(captured.Count).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_ReturnsWhenMoreThanRequestedArrivedAsync(
      CancellationToken cancellationToken) {
    // The count is a floor, not an exact match — a consumer that wrote an extra message must
    // not leave the wait parked forever.
    var coordinator = new CapturingWorkCoordinator();
    await coordinator.StoreInboxMessagesAsync([_message(), _message(), _message()], cancellationToken: CancellationToken.None);

    var captured = await coordinator.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));

    await Assert.That(captured.Count).IsEqualTo(3);
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_WakesWhenTheMessagesArriveWhileParkedAsync(
      CancellationToken cancellationToken) {
    // The reason this exists instead of a delay: the wait is released by the write itself, so
    // the test finishes as soon as the work is done rather than after a guessed interval.
    var coordinator = new CapturingWorkCoordinator();
    var waiting = coordinator.WaitForInboxAsync(2, TimeSpan.FromSeconds(20));

    await coordinator.StoreInboxMessagesAsync([_message()], cancellationToken: CancellationToken.None);
    await coordinator.StoreInboxMessagesAsync([_message()], cancellationToken: CancellationToken.None);

    var captured = await waiting;
    await Assert.That(captured.Count).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_KeepsWaitingUntilTheCountIsReachedAsync(
      CancellationToken cancellationToken) {
    // A write that does not satisfy the count must re-park rather than return short — returning
    // the partial capture is the failure mode that makes a caller's assertions pass on half the
    // messages.
    var coordinator = new CapturingWorkCoordinator();
    var waiting = coordinator.WaitForInboxAsync(3, TimeSpan.FromSeconds(20));

    await coordinator.StoreInboxMessagesAsync([_message()], cancellationToken: CancellationToken.None);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("one of three is not enough — returning here would hand back a partial capture");

    await coordinator.StoreInboxMessagesAsync([_message(), _message()], cancellationToken: CancellationToken.None);

    var captured = await waiting;
    await Assert.That(captured.Count).IsEqualTo(3);
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_TimesOutWithTheCountItActuallySawAsync(
      CancellationToken cancellationToken) {
    // The whole diagnostic value of the timeout is the shortfall: "expected 5, saw 2" tells the
    // author the consumer ran and under-produced, where a bare timeout does not.
    var coordinator = new CapturingWorkCoordinator();
    await coordinator.StoreInboxMessagesAsync([_message(), _message()], cancellationToken: CancellationToken.None);

    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await coordinator.WaitForInboxAsync(5, TimeSpan.FromMilliseconds(200)));

    await Assert.That(ex!.Message).Contains("5");
    await Assert.That(ex.Message).Contains("2")
      .Because("the shortfall is what tells the author the consumer ran and under-produced");
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_ForZeroMessages_ReturnsImmediatelyAsync(
      CancellationToken cancellationToken) {
    var coordinator = new CapturingWorkCoordinator();

    var captured = await coordinator.WaitForInboxAsync(0, TimeSpan.FromSeconds(10));

    await Assert.That(captured).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForInbox_ServesConcurrentWaitersAsync(CancellationToken cancellationToken) {
    // The signal is replaced on each write, so a waiter that captured the old one has to be
    // released by it rather than left holding a source nobody will complete again.
    var coordinator = new CapturingWorkCoordinator();
    var first = coordinator.WaitForInboxAsync(2, TimeSpan.FromSeconds(20));
    var second = coordinator.WaitForInboxAsync(2, TimeSpan.FromSeconds(20));

    await coordinator.StoreInboxMessagesAsync([_message(), _message()], cancellationToken: CancellationToken.None);

    var results = await Task.WhenAll(first, second);
    await Assert.That(results[0].Count).IsEqualTo(2);
    await Assert.That(results[1].Count).IsEqualTo(2);
  }

  [Test]
  public async Task ClaimWork_ReturnsAnEmptyBatchAsync() {
    // The double captures writes; it never hands work back. An empty batch in every lane is
    // what stops a worker under test from looping on phantom work.
    var coordinator = new CapturingWorkCoordinator();

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest((Guid)TrackedGuid.NewMedo(), "svc", "host", ProcessId: 1));

    await Assert.That(batch.OutboxWork).IsEmpty();
    await Assert.That(batch.InboxWork).IsEmpty();
    await Assert.That(batch.PerspectiveWork).IsEmpty();
    await Assert.That(batch.SyncInquiryResults).IsNull();
  }

  [Test]
  public async Task TheInertMembers_AreNoOpsAsync() {
    // Everything other than the inbox capture is deliberately inert, so a worker under test can
    // run its full cycle without the double inventing state.
    var coordinator = new CapturingWorkCoordinator();

    await coordinator.StoreOutboxMessagesAsync([], partitionCount: 2);
    await coordinator.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "AnyPerspective",
      LastEventId = (Guid)TrackedGuid.NewMedo(),
      Status = PerspectiveProcessingStatus.Completed,
    });
    await coordinator.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "AnyPerspective",
      LastEventId = (Guid)TrackedGuid.NewMedo(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "inert",
    });
    await coordinator.DeregisterInstanceAsync((Guid)TrackedGuid.NewMedo());
    var stats = await coordinator.GatherStatisticsAsync();
    var cursor = await coordinator.GetPerspectiveCursorAsync(
      (Guid)TrackedGuid.NewMedo(), "AnyPerspective");

    await Assert.That(stats).IsNotNull();
    await Assert.That(cursor).IsNull();
    await Assert.That(coordinator.StoredInboxMessages).IsEmpty()
      .Because("only the inbox lane captures — the others must not leak into what a test asserts");
  }
}
