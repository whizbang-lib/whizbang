using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Testing;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// That a row a perspective cannot read is classified, announced once per stream, counted, and
/// parked with backoff in the database instead of retried every cycle.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the read failure surfaced as a generic error per drain cycle with a stack trace as
/// the only clue, and the rows were never marked failed: the cursor failure carried no event id,
/// the coordinator skipped it, so the lease lapsed, the rows were re-claimed, and the same stream
/// failed again on the next cycle, forever.
/// </para>
/// <para>
/// Now the failure is classified by type (<see cref="StoredFormUnreadable"/>), logged at Error
/// once per perspective and stream with the path and the refusal, counted on the perspective
/// meter, and each leased row of the group is reported through the failure channel so the
/// database records the failure, schedules the retry with backoff, and dead-letters the row at
/// the configured threshold. A stream that reads again is released.
/// </para>
/// </remarks>
public partial class PerspectiveWorkerDeepPathDrainTests {
  private const int STORED_FORM_UNREADABLE_EVENT_ID = 65;
  private const int STORED_FORM_UNREADABLE_AGAIN_EVENT_ID = 66;

  /// <summary>The refusal a real reader raises, with the path the serializer adds.</summary>
  private static JsonException _storedFormRefusal() {
    try {
      JsonSerializer.Deserialize("{\"At\":true}", HolderContext.Default.Holder);
    } catch (JsonException ex) {
      return ex;
    }
    throw new InvalidOperationException("the fixture did not fail");
  }

  /// <summary>A runner whose next call throws what it is told to, or completes.</summary>
  private sealed class StoredFormRunner : IPerspectiveRunner {
    private int _calls;
    public Exception? NextException { get; set; }
    public int Calls => Volatile.Read(ref _calls);
    public Type PerspectiveType => typeof(StoredFormRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_completed(streamId, perspectiveName, lastProcessedEventId ?? Guid.Empty));

    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      if (NextException is { } exception) {
        throw exception;
      }
      return Task.FromResult(_completed(streamId, perspectiveName, events[^1].MessageId.Value));
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_completed(streamId, perspectiveName, triggeringEventId));

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    private static PerspectiveCursorCompletion _completed(Guid streamId, string perspectiveName, Guid lastEventId) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.Completed
    };
  }

  private static (StoredFormRunner Runner, MultiRunnerRegistry Registry) _storedFormRunner() {
    var runner = new StoredFormRunner();
    var registry = new MultiRunnerRegistry([typeof(DrainDeepEvent)]);
    registry.Add(PERSPECTIVE, runner);
    return (runner, registry);
  }

  /// <summary>
  /// The first unreadable row on a stream is logged once at Error with the path, counted, and its
  /// leased rows reported through the failure channel so the database parks them.
  /// </summary>
  [Test]
  public async Task DrainMode_UnreadableStoredForm_IsAnnouncedCountedAndParkedAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("unreadable"))]);
    var (runner, registry) = _storedFormRunner();
    runner.NextException = _storedFormRefusal();
    var logger = new FakeLogger<PerspectiveWorker>();
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var failures = new StoredFormFailureRegistry();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, logger: logger, metrics: metrics, storedFormFailures: failures);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.FailureCapture.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var announced = logger.Collector.GetSnapshot().Where(r => r.Id.Id == STORED_FORM_UNREADABLE_EVENT_ID).ToList();
    await Assert.That(announced).Count().IsEqualTo(1);
    await Assert.That(announced[0].Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Error);
    await Assert.That(announced[0].Message).Contains(PERSPECTIVE, StringComparison.Ordinal);
    await Assert.That(announced[0].Message).Contains(streamId.ToString(), StringComparison.Ordinal);
    await Assert.That(announced[0].Message).Contains("$.At", StringComparison.Ordinal)
      .Because("the path is what tells the operator which value, and which rewrite, is missing");
    await Assert.That(announced[0].Message).Contains("must be a number (microseconds) or a rendering", StringComparison.Ordinal)
      .Because("the forms accepted and the token found are what tells the operator it is a stored-form problem");
    await Assert.That(announced[0].Exception).IsNotNull();

    var (category, parked) = harness.FailureCapture.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(parked.MessageId).IsEqualTo(workId)
      .Because("the failure is recorded against the leased row, which is what the database backs off and dead-letters");
    await Assert.That(parked.Reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(parked.Error).Contains("$.At", StringComparison.Ordinal);

    var counted = meters.GetByName("whizbang.perspective.read_failures")
      .Where(m => m.Tags.GetValueOrDefault("perspective_name") == PERSPECTIVE)
      .ToList();
    await Assert.That(counted).Count().IsEqualTo(1);
    await Assert.That(counted[0].Tags["reason"]).IsEqualTo(StoredFormUnreadable.REASON);

    await Assert.That(failures.Count).IsEqualTo(1);
    coordinator.Failures.TryPeek(out var cursorFailure);
    await Assert.That(cursorFailure?.Error).Contains("$.At", StringComparison.Ordinal)
      .Because("the cursor failure still carries the refusal for anything that reads it");
  }

  /// <summary>
  /// The same stream failing again is logged at Debug, its new rows parked too, and a stream that
  /// reads again is released.
  /// </summary>
  [Test]
  public async Task DrainMode_UnreadableStoredForm_AgainIsQuietAndRecoveryReleasesAsync() {
    var streamId = Guid.CreateVersion7();
    var firstEvent = Guid.CreateVersion7();
    var secondEvent = Guid.CreateVersion7();
    var thirdEvent = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, firstEvent, Guid.CreateVersion7())]);
    coordinator.EnqueueStreamEvents([_raw(streamId, secondEvent, Guid.CreateVersion7())]);
    coordinator.EnqueueStreamEvents([_raw(streamId, thirdEvent, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(firstEvent, new DrainDeepEvent("first"))]);
    eventStore.EnqueueDeserialized([_envelope(secondEvent, new DrainDeepEvent("second"))]);
    eventStore.EnqueueDeserialized([_envelope(thirdEvent, new DrainDeepEvent("third"))]);
    var (runner, registry) = _storedFormRunner();
    runner.NextException = _storedFormRefusal();
    var logger = new FakeLogger<PerspectiveWorker>();
    var failures = new StoredFormFailureRegistry();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, logger: logger, storedFormFailures: failures);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.FailureCapture.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.FailureCapture.WaitForCountAsync(2, TimeSpan.FromSeconds(10));
    runner.NextException = null;
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Count(r => r.Id.Id == STORED_FORM_UNREADABLE_EVENT_ID)).IsEqualTo(1)
      .Because("an operator sees one error per stream, not a wall of identical ones");
    var again = records.Where(r => r.Id.Id == STORED_FORM_UNREADABLE_AGAIN_EVENT_ID).ToList();
    await Assert.That(again).Count().IsEqualTo(1);
    await Assert.That(again[0].Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Debug);
    await Assert.That(harness.FailureCapture.Items.Count).IsEqualTo(2)
      .Because("every leased row of a stream that cannot be read is parked, the second time as well");
    await Assert.That(runner.Calls).IsEqualTo(3);
    await Assert.That(failures.Count).IsEqualTo(0)
      .Because("a stream that reads again is released, so a later failure is announced again");
  }

  /// <summary>
  /// Any other failure keeps its generic error, is not counted as a stored-form failure, and still
  /// parks the leased rows it failed on.
  /// </summary>
  /// <remarks>
  /// The cursor failure the drain path reported carried no event id, so the coordinator recorded
  /// nothing against the rows: the lease lapsed, the rows were re-claimed, and the same failure
  /// repeated every cycle without backoff and without ever reaching the dead-letter threshold.
  /// The stored-form path parked its rows; every other failure parks its rows the same way.
  /// </remarks>
  [Test]
  public async Task DrainMode_OtherFailure_IsNotClassifiedAsStoredFormButParksItsRowsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("other"))]);
    var (runner, registry) = _storedFormRunner();
    runner.NextException = new InvalidOperationException("apply failed for another reason");
    var logger = new FakeLogger<PerspectiveWorker>();
    var failures = new StoredFormFailureRegistry();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, logger: logger, storedFormFailures: failures);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.FailureCapture.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Any(r => r.Id.Id == STORED_FORM_UNREADABLE_EVENT_ID)).IsFalse();
    await Assert.That(records.Any(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Error
      && r.Message.Contains("Error processing perspective", StringComparison.Ordinal))).IsTrue();
    await Assert.That(failures.Count).IsEqualTo(0);

    var (category, parked) = harness.FailureCapture.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(parked.MessageId).IsEqualTo(workId)
      .Because("the failure is recorded against the leased row so the database backs it off and dead-letters it; "
        + "a cursor failure with no event id records nothing and the row is re-claimed forever");
    await Assert.That(parked.Reason).IsEqualTo(MessageFailureReason.Unknown);
    await Assert.That(parked.Error).Contains("apply failed for another reason", StringComparison.Ordinal);
  }
}
