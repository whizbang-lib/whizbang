using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

#pragma warning disable RCS1118 // Mark local variable as const — conflicts with TUnit's TUnitAssertions0005 (can't pass const to Assert.That)

/// <summary>
/// Unit tests for perspective snapshot options, stream lock options,
/// processing status flags, and system event records.
/// </summary>
public class PerspectiveSnapshotAndRewindTests {

  #region PerspectiveSnapshotOptions Tests

  [Test]
  public async Task PerspectiveSnapshotOptions_Defaults_HaveExpectedValuesAsync() {
    var options = new PerspectiveSnapshotOptions();

    await Assert.That(options.SnapshotEveryNEvents).IsEqualTo(100);
    await Assert.That(options.MaxSnapshotsPerStream).IsEqualTo(5);
    await Assert.That(options.Enabled).IsTrue();
  }

  [Test]
  public async Task PerspectiveSnapshotOptions_CustomValues_ArePreservedAsync() {
    var options = new PerspectiveSnapshotOptions {
      SnapshotEveryNEvents = 50,
      MaxSnapshotsPerStream = 10,
      Enabled = false
    };

    await Assert.That(options.SnapshotEveryNEvents).IsEqualTo(50);
    await Assert.That(options.MaxSnapshotsPerStream).IsEqualTo(10);
    await Assert.That(options.Enabled).IsFalse();
  }

  #endregion

  #region PerspectiveStreamLockOptions Tests

  [Test]
  public async Task PerspectiveStreamLockOptions_Defaults_HaveExpectedValuesAsync() {
    var options = new PerspectiveStreamLockOptions();

    await Assert.That(options.LockTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(10));
  }

  [Test]
  public async Task PerspectiveStreamLockOptions_CustomValues_ArePreservedAsync() {
    var options = new PerspectiveStreamLockOptions {
      LockTimeout = TimeSpan.FromMinutes(2),
      KeepAliveInterval = TimeSpan.FromSeconds(30)
    };

    await Assert.That(options.LockTimeout).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  #endregion

  #region PerspectiveProcessingStatus RewindRequired Tests

  [Test]
  public async Task PerspectiveProcessingStatus_RewindRequired_IsBit5Async() {
    var status = PerspectiveProcessingStatus.RewindRequired;

    await Assert.That((int)status).IsEqualTo(32); // 1 << 5
  }

  [Test]
  public async Task PerspectiveProcessingStatus_RewindRequired_CanBeCombinedWithOtherFlagsAsync() {
    const PerspectiveProcessingStatus status = PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.RewindRequired;

    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.Completed)).IsTrue();
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Aggregate_DetectsRewindRequiredInGroupAsync() {
    var statuses = new[] {
      PerspectiveProcessingStatus.None,
      PerspectiveProcessingStatus.Completed,
      PerspectiveProcessingStatus.RewindRequired
    };

    var aggregated = statuses.Aggregate(PerspectiveProcessingStatus.None, (acc, s) => acc | s);

    await Assert.That(aggregated.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Aggregate_NoRewindRequired_WhenNotPresentAsync() {
    var statuses = new[] {
      PerspectiveProcessingStatus.None,
      PerspectiveProcessingStatus.Completed
    };

    var aggregated = statuses.Aggregate(PerspectiveProcessingStatus.None, (acc, s) => acc | s);

    await Assert.That(aggregated.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsFalse();
  }

  #endregion

  #region PerspectiveCursorInfo RewindTriggerEventId Tests

  [Test]
  public async Task PerspectiveCursorInfo_RewindTriggerEventId_DefaultsToNullAsync() {
    var cursorInfo = new PerspectiveCursorInfo {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.Completed
    };

    await Assert.That(cursorInfo.RewindTriggerEventId).IsNull();
  }

  [Test]
  public async Task PerspectiveCursorInfo_RewindTriggerEventId_CanBeSetAsync() {
    var triggerEventId = Guid.CreateVersion7();
    var cursorInfo = new PerspectiveCursorInfo {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    await Assert.That(cursorInfo.RewindTriggerEventId).IsEqualTo(triggerEventId);
  }

  #endregion

  #region PerspectiveWork WorkId Tests

  [Test]
  public async Task PerspectiveWork_WorkId_DefaultsToEmptyGuidAsync() {
    var work = new PerspectiveWork {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "TestPerspective",
      PartitionNumber = 1
    };

    await Assert.That(work.WorkId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task PerspectiveWork_WorkId_CanBeSetAsync() {
    var workId = Guid.CreateVersion7();
    var work = new PerspectiveWork {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "TestPerspective",
      PartitionNumber = 1,
      WorkId = workId
    };

    await Assert.That(work.WorkId).IsEqualTo(workId);
  }

  #endregion

  #region PerspectiveEventCompletion Tests

  [Test]
  public async Task PerspectiveEventCompletion_StatusFlags_DefaultsToCompletedAsync() {
    var completion = new PerspectiveEventCompletion {
      EventWorkId = Guid.CreateVersion7()
    };

    await Assert.That(completion.StatusFlags).IsEqualTo((int)PerspectiveProcessingStatus.Completed);
  }

  [Test]
  public async Task PerspectiveEventCompletion_EventWorkId_IsSetCorrectlyAsync() {
    var workId = Guid.CreateVersion7();
    var completion = new PerspectiveEventCompletion {
      EventWorkId = workId
    };

    await Assert.That(completion.EventWorkId).IsEqualTo(workId);
  }

  #endregion

  #region System Event Tests

  [Test]
  public async Task PerspectiveRewindStarted_Properties_AreCorrectAsync() {
    var streamId = Guid.CreateVersion7();
    var triggeringEventId = Guid.CreateVersion7();
    var snapshotEventId = Guid.CreateVersion7();
    var startedAt = DateTimeOffset.UtcNow;

    var evt = new PerspectiveRewindStarted(
      streamId, "OrderPerspective", triggeringEventId, snapshotEventId, true, startedAt);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("OrderPerspective");
    await Assert.That(evt.TriggeringEventId).IsEqualTo(triggeringEventId);
    await Assert.That(evt.ReplayFromSnapshotEventId).IsEqualTo(snapshotEventId);
    await Assert.That(evt.HasSnapshot).IsTrue();
    await Assert.That(evt.StartedAt).IsEqualTo(startedAt);
  }

  [Test]
  public async Task PerspectiveRewindStarted_WithoutSnapshot_HasNullSnapshotEventIdAsync() {
    var evt = new PerspectiveRewindStarted(
      Guid.CreateVersion7(), "TestPerspective", Guid.CreateVersion7(), null, false, DateTimeOffset.UtcNow);

    await Assert.That(evt.ReplayFromSnapshotEventId).IsNull();
    await Assert.That(evt.HasSnapshot).IsFalse();
  }

  [Test]
  public async Task PerspectiveRewindCompleted_Properties_AreCorrectAsync() {
    var streamId = Guid.CreateVersion7();
    var triggeringEventId = Guid.CreateVersion7();
    var finalEventId = Guid.CreateVersion7();
    var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
    var completedAt = DateTimeOffset.UtcNow;

    var evt = new PerspectiveRewindCompleted(
      streamId, "OrderPerspective", triggeringEventId, finalEventId, 150, startedAt, completedAt);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("OrderPerspective");
    await Assert.That(evt.TriggeringEventId).IsEqualTo(triggeringEventId);
    await Assert.That(evt.FinalEventId).IsEqualTo(finalEventId);
    await Assert.That(evt.EventsReplayed).IsEqualTo(150);
    await Assert.That(evt.StartedAt).IsEqualTo(startedAt);
    await Assert.That(evt.CompletedAt).IsEqualTo(completedAt);
  }

  [Test]
  public async Task PerspectiveRewindStarted_ImplementsIEventAsync() {
    var evt = new PerspectiveRewindStarted(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(), null, false, DateTimeOffset.UtcNow);

    await Assert.That(evt is IEvent).IsTrue();
  }

  [Test]
  public async Task PerspectiveRewindCompleted_ImplementsIEventAsync() {
    var evt = new PerspectiveRewindCompleted(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(), Guid.CreateVersion7(), 0,
      DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    await Assert.That(evt is IEvent).IsTrue();
  }

  #endregion

  #region IPerspectiveSnapshotStore Interface Contract Tests

  [Test]
  public async Task IPerspectiveSnapshotStore_HasExpectedMethodsAsync() {
    // Verify the interface shape via reflection (AOT-safe: this is test code)
    var interfaceType = typeof(IPerspectiveSnapshotStore);
    var methods = interfaceType.GetMethods();

    var methodNames = methods.Select(m => m.Name).ToArray();

    await Assert.That(methodNames).Contains("CreateSnapshotAsync");
    await Assert.That(methodNames).Contains("GetLatestSnapshotAsync");
    await Assert.That(methodNames).Contains("GetLatestSnapshotBeforeAsync");
    await Assert.That(methodNames).Contains("HasAnySnapshotAsync");
    await Assert.That(methodNames).Contains("PruneOldSnapshotsAsync");
    await Assert.That(methodNames).Contains("DeleteAllSnapshotsAsync");
  }

  #endregion

  #region IPerspectiveStreamLocker Interface Contract Tests

  [Test]
  public async Task IPerspectiveStreamLocker_HasExpectedMethodsAsync() {
    var interfaceType = typeof(IPerspectiveStreamLocker);
    var methods = interfaceType.GetMethods();

    var methodNames = methods.Select(m => m.Name).ToArray();

    await Assert.That(methodNames).Contains("TryAcquireLockAsync");
    await Assert.That(methodNames).Contains("RenewLockAsync");
    await Assert.That(methodNames).Contains("ReleaseLockAsync");
  }

  #endregion

  #region IPerspectiveRunner Interface Contract Tests

  [Test]
  public async Task IPerspectiveRunner_HasRewindAndRunAsyncMethodAsync() {
    var interfaceType = typeof(IPerspectiveRunner);
    // Slice 26.11 added a second overload taking long? triggeringCommitSequence;
    // specify the original 4-arg overload so this stays unambiguous.
    var method = interfaceType.GetMethod(
      "RewindAndRunAsync",
      [typeof(Guid), typeof(string), typeof(Guid), typeof(CancellationToken)]);

    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task<PerspectiveCursorCompletion>));
  }

  [Test]
  public async Task IPerspectiveRunner_HasBootstrapSnapshotAsyncMethodAsync() {
    var interfaceType = typeof(IPerspectiveRunner);
    var method = interfaceType.GetMethod("BootstrapSnapshotAsync");

    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
  }

  #endregion

  #region PerspectiveProcessingStatus RewindRequired — Additional Coverage

  [Test]
  public async Task PerspectiveProcessingStatus_RewindRequired_IsNotPartOfCompletedAsync() {
    const PerspectiveProcessingStatus status = PerspectiveProcessingStatus.Completed;
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsFalse();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_RewindRequired_IsNotPartOfNoneAsync() {
    const PerspectiveProcessingStatus status = PerspectiveProcessingStatus.None;
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsFalse();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_RewindRequired_CanBeCombinedWithFailedAndCatchingUpAsync() {
    const PerspectiveProcessingStatus status = PerspectiveProcessingStatus.Failed
      | PerspectiveProcessingStatus.CatchingUp
      | PerspectiveProcessingStatus.RewindRequired;

    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.Failed)).IsTrue();
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.CatchingUp)).IsTrue();
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsTrue();
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.Completed)).IsFalse();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_BitwiseOr_WithRewindRequired_PreservesOtherFlagsAsync() {
    var original = PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.CatchingUp;
    var withRewind = original | PerspectiveProcessingStatus.RewindRequired;

    await Assert.That((int)withRewind).IsEqualTo((int)original | 32);
    await Assert.That(withRewind.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsTrue();
    await Assert.That(withRewind.HasFlag(PerspectiveProcessingStatus.Completed)).IsTrue();
    await Assert.That(withRewind.HasFlag(PerspectiveProcessingStatus.CatchingUp)).IsTrue();
  }

  #endregion

  #region PerspectiveCursorInfo — Additional Coverage

  [Test]
  public async Task PerspectiveCursorInfo_AllProperties_CanBeSetAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();

    var cursor = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "Test",
      LastEventId = eventId,
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    await Assert.That(cursor.StreamId).IsEqualTo(streamId);
    await Assert.That(cursor.PerspectiveName).IsEqualTo("Test");
    await Assert.That(cursor.LastEventId).IsEqualTo(eventId);
    await Assert.That(cursor.Status).IsEqualTo(PerspectiveProcessingStatus.RewindRequired);
    await Assert.That(cursor.RewindTriggerEventId).IsEqualTo(triggerEventId);
  }

  [Test]
  public async Task PerspectiveCursorInfo_Status_DetectsRewindRequiredAsync() {
    var cursor = new PerspectiveCursorInfo {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired | PerspectiveProcessingStatus.Completed
    };

    await Assert.That(cursor.Status.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsTrue();
  }

  #endregion

  #region PerspectiveSnapshotOptions — Additional Coverage

  [Test]
  public async Task PerspectiveSnapshotOptions_Enabled_DefaultsTrueAsync() {
    var options = new PerspectiveSnapshotOptions();
    await Assert.That(options.Enabled).IsTrue();
  }

  [Test]
  public async Task PerspectiveSnapshotOptions_DisabledPreventsSnapshotsAsync() {
    var options = new PerspectiveSnapshotOptions { Enabled = false };
    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.SnapshotEveryNEvents).IsEqualTo(100); // Other defaults preserved
  }

  [Test]
  public async Task PerspectiveSnapshotOptions_ZeroEventsThreshold_IsValidAsync() {
    var options = new PerspectiveSnapshotOptions { SnapshotEveryNEvents = 0 };
    await Assert.That(options.SnapshotEveryNEvents).IsEqualTo(0);
  }

  [Test]
  public async Task PerspectiveSnapshotOptions_LargeValues_ArePreservedAsync() {
    var options = new PerspectiveSnapshotOptions {
      SnapshotEveryNEvents = 10_000,
      MaxSnapshotsPerStream = 100
    };
    await Assert.That(options.SnapshotEveryNEvents).IsEqualTo(10_000);
    await Assert.That(options.MaxSnapshotsPerStream).IsEqualTo(100);
  }

  #endregion

  #region PerspectiveStreamLockOptions — Additional Coverage

  [Test]
  public async Task PerspectiveStreamLockOptions_KeepAliveIsLessThanHalfLockTimeoutAsync() {
    var options = new PerspectiveStreamLockOptions();
    // KeepAlive (10s) should be less than half of LockTimeout (30s) to prevent expiry
    await Assert.That(options.KeepAliveInterval).IsLessThan(TimeSpan.FromTicks(options.LockTimeout.Ticks / 2));
  }

  [Test]
  public async Task PerspectiveStreamLockOptions_CustomValues_IndependentAsync() {
    var options = new PerspectiveStreamLockOptions {
      LockTimeout = TimeSpan.FromMinutes(5),
      KeepAliveInterval = TimeSpan.FromSeconds(60)
    };
    await Assert.That(options.LockTimeout).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(options.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(60));
  }

  #endregion

  #region System Events — Additional Coverage

  [Test]
  public async Task PerspectiveRewindStarted_WithAllParams_PreservesValuesAsync() {
    var streamId = Guid.CreateVersion7();
    var triggeringEventId = Guid.CreateVersion7();
    var snapshotEventId = Guid.CreateVersion7();
    var startedAt = DateTimeOffset.UtcNow;

    var evt = new PerspectiveRewindStarted(
      streamId, "TestPerspective", triggeringEventId, snapshotEventId, true, startedAt);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("TestPerspective");
    await Assert.That(evt.TriggeringEventId).IsEqualTo(triggeringEventId);
    await Assert.That(evt.ReplayFromSnapshotEventId).IsEqualTo(snapshotEventId);
    await Assert.That(evt.HasSnapshot).IsTrue();
    await Assert.That(evt.StartedAt).IsEqualTo(startedAt);
  }

  [Test]
  public async Task PerspectiveRewindCompleted_DurationCanBeCalculatedAsync() {
    var startedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
    var completedAt = DateTimeOffset.UtcNow;

    var evt = new PerspectiveRewindCompleted(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(), Guid.CreateVersion7(), 500,
      startedAt, completedAt);

    var duration = evt.CompletedAt - evt.StartedAt;
    await Assert.That(duration.TotalSeconds).IsGreaterThanOrEqualTo(9);
  }

  [Test]
  public async Task PerspectiveRewindCompleted_ZeroEventsReplayed_IsValidAsync() {
    var evt = new PerspectiveRewindCompleted(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(), Guid.CreateVersion7(), 0,
      DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    await Assert.That(evt.EventsReplayed).IsEqualTo(0);
  }

  [Test]
  public async Task PerspectiveRewindStarted_NullSnapshot_IndicatesFullReplayAsync() {
    var evt = new PerspectiveRewindStarted(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(), null, false, DateTimeOffset.UtcNow);

    await Assert.That(evt.ReplayFromSnapshotEventId).IsNull();
    await Assert.That(evt.HasSnapshot).IsFalse();
  }

  #endregion

  #region PerspectiveEventCompletion — Additional Coverage

  [Test]
  public async Task PerspectiveEventCompletion_RewindRequired_StatusFlagCanBeSetAsync() {
    var completion = new PerspectiveEventCompletion {
      EventWorkId = Guid.CreateVersion7(),
      StatusFlags = (int)(PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.RewindRequired)
    };

    var status = (PerspectiveProcessingStatus)completion.StatusFlags;
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.RewindRequired)).IsTrue();
    await Assert.That(status.HasFlag(PerspectiveProcessingStatus.Completed)).IsTrue();
  }

  [Test]
  public async Task PerspectiveEventCompletion_DefaultStatusFlags_IsCompletedValueAsync() {
    var completion = new PerspectiveEventCompletion { EventWorkId = Guid.CreateVersion7() };
    await Assert.That(completion.StatusFlags).IsEqualTo(2); // PerspectiveProcessingStatus.Completed = 1 << 1 = 2
  }

  #endregion

  #region PerspectiveWork WorkId — Additional Coverage

  [Test]
  public async Task PerspectiveWork_MultipleWorkIds_AreDistinctAsync() {
    var work1 = new PerspectiveWork {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test",
      PartitionNumber = 1,
      WorkId = Guid.CreateVersion7()
    };
    var work2 = new PerspectiveWork {
      StreamId = work1.StreamId,
      PerspectiveName = "Test",
      PartitionNumber = 1,
      WorkId = Guid.CreateVersion7()
    };

    await Assert.That(work1.WorkId).IsNotEqualTo(work2.WorkId);
    await Assert.That(work1.WorkId).IsNotEqualTo(Guid.Empty);
    await Assert.That(work2.WorkId).IsNotEqualTo(Guid.Empty);
  }

  #endregion

  #region IPerspectiveSnapshotStore — Additional Coverage

  [Test]
  public async Task IPerspectiveSnapshotStore_GetLatestSnapshotBeforeAsync_ExistsAsync() {
    var interfaceType = typeof(IPerspectiveSnapshotStore);
    var method = interfaceType.GetMethod("GetLatestSnapshotBeforeAsync");

    await Assert.That(method).IsNotNull();
  }

  [Test]
  public async Task IPerspectiveSnapshotStore_DeleteAllSnapshotsAsync_ExistsAsync() {
    var interfaceType = typeof(IPerspectiveSnapshotStore);
    var method = interfaceType.GetMethod("DeleteAllSnapshotsAsync");

    await Assert.That(method).IsNotNull();
  }

  /// <summary>
  /// Production forensic G7 default-impl: stores that haven't overridden the new method get a fall-through
  /// to <see cref="IPerspectiveSnapshotStore.GetLatestSnapshotAsync"/>, with null commit_sequence.
  /// </summary>
  [Test]
  public async Task GetLatestSnapshotWithCommitSequenceAsync_DefaultImpl_FallsThroughToLegacyAsync() {
    var streamId = Guid.NewGuid();
    var snapshotEventId = Guid.NewGuid();
    using var snapshotJson = System.Text.Json.JsonDocument.Parse("""{"k":"v"}""");
    IPerspectiveSnapshotStore store = new _LegacyOnlySnapshotStore {
      Result = (snapshotEventId, snapshotJson)
    };

    var actual = await store.GetLatestSnapshotWithCommitSequenceAsync(streamId, "p");

    await Assert.That(actual).IsNotNull();
    await Assert.That(actual!.Value.SnapshotEventId).IsEqualTo(snapshotEventId);
    await Assert.That(actual.Value.SnapshotCommitSequence)
      .IsNull()
      .Because("the default-impl can't synthesize a commit_sequence — fall-through stores get null");
  }

  /// <summary>Companion: default-impl returns null when underlying legacy returns null.</summary>
  [Test]
  public async Task GetLatestSnapshotWithCommitSequenceAsync_DefaultImpl_LegacyReturnsNullAsync() {
    IPerspectiveSnapshotStore store = new _LegacyOnlySnapshotStore { Result = null };

    var actual = await store.GetLatestSnapshotWithCommitSequenceAsync(Guid.NewGuid(), "p");

    await Assert.That(actual).IsNull();
  }

  /// <summary>
  /// Minimal stub implementing only the legacy interface methods. Does NOT override
  /// the production forensic G7 sibling — so calls fall through to the interface default-impl, which is
  /// the path this test exercises.
  /// </summary>
  private sealed class _LegacyOnlySnapshotStore : IPerspectiveSnapshotStore {
    public (Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)? Result { get; set; }
    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, System.Text.Json.JsonDocument snapshotData, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Result);
    public Task<(Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, System.Text.Json.JsonDocument)?>(null);
    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult(false);
    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.CompletedTask;
  }

  #endregion

  #region IPerspectiveRunner — Additional Coverage

  [Test]
  public async Task IPerspectiveRunner_RewindAndRunAsync_HasCorrectParametersAsync() {
    var interfaceType = typeof(IPerspectiveRunner);
    var method = interfaceType.GetMethod(
      "RewindAndRunAsync",
      [typeof(Guid), typeof(string), typeof(Guid), typeof(CancellationToken)]);
    var parameters = method!.GetParameters();

    await Assert.That(parameters.Length).IsEqualTo(4); // streamId, perspectiveName, triggeringEventId, ct
    await Assert.That(parameters[0].Name).IsEqualTo("streamId");
    await Assert.That(parameters[1].Name).IsEqualTo("perspectiveName");
    await Assert.That(parameters[2].Name).IsEqualTo("triggeringEventId");
    await Assert.That(parameters[3].Name).IsEqualTo("cancellationToken");
  }

  [Test]
  public async Task IPerspectiveRunner_BootstrapSnapshotAsync_HasCorrectParametersAsync() {
    var interfaceType = typeof(IPerspectiveRunner);
    var method = interfaceType.GetMethod("BootstrapSnapshotAsync");
    var parameters = method!.GetParameters();

    await Assert.That(parameters.Length).IsEqualTo(4); // streamId, perspectiveName, lastProcessedEventId, ct
    await Assert.That(parameters[0].Name).IsEqualTo("streamId");
    await Assert.That(parameters[1].Name).IsEqualTo("perspectiveName");
    await Assert.That(parameters[2].Name).IsEqualTo("lastProcessedEventId");
  }

  #endregion
}

#pragma warning restore RCS1118
