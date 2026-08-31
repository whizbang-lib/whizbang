using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveWorker rewind routing, bootstrap snapshot detection,
/// and stream lock integration.
/// Covers the worker's decision tree:
///   RewindRequired → rewind path (with lock)
///   No flag + no snapshots → bootstrap + normal
///   Normal → no lock
/// </summary>
public class PerspectiveWorkerRewindTests {

  #region Rewind Routing Tests

  [Test]
  public async Task Worker_RewindRequired_CallsRewindAndRunAsyncAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);

    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.RewindRequired,
        WorkId = Guid.CreateVersion7()
      }
    ];

    // Provide cursor with RewindTriggerEventId
    coordinator.CursorOverrides[("TestPerspective", streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - RewindAndRunAsync was called
    await Assert.That(runner.RewindAndRunCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(runner.LastTriggeringEventId).IsEqualTo(triggerEventId);
  }

  [Test]
  public async Task Worker_NormalPath_CallsRunAsyncAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.None,
        WorkId = Guid.CreateVersion7()
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - RunAsync was called, RewindAndRunAsync was NOT called
    await Assert.That(runner.RunCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(runner.RewindAndRunCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task Worker_RewindRequired_WithLock_AcquiresAndReleasesLockAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);
    var locker = new FakePerspectiveStreamLocker();

    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.RewindRequired,
        WorkId = Guid.CreateVersion7()
      }
    ];

    coordinator.CursorOverrides[("TestPerspective", streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      streamLocker: locker,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Lock was acquired and released
    await Assert.That(locker.AcquireCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(locker.ReleaseCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(locker.LastReason).IsEqualTo("rewind");
  }

  [Test]
  public async Task Worker_RewindRequired_LockFails_DefersProcessingAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);
    var locker = new FakePerspectiveStreamLocker { AcquireResult = false };

    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.RewindRequired,
        WorkId = Guid.CreateVersion7()
      }
    ];

    coordinator.CursorOverrides[("TestPerspective", streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      streamLocker: locker,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - RewindAndRunAsync was NOT called (lock failed, processing deferred)
    await Assert.That(runner.RewindAndRunCallCount).IsEqualTo(0);
    // Lock was attempted but not released (never acquired)
    await Assert.That(locker.AcquireCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(locker.ReleaseCallCount).IsEqualTo(0);
  }

  #endregion

  #region Bootstrap Snapshot Tests

  [Test]
  public async Task Worker_NormalPath_WithSnapshotStore_NoSnapshots_CallsBootstrapAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);
    var snapshotStore = new FakeSnapshotStore { HasSnapshots = false };

    var streamId = Guid.CreateVersion7();
    var lastEventId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = lastEventId,
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.None,
        WorkId = Guid.CreateVersion7()
      }
    ];

    // Provide cursor so lastProcessedEventId is available for bootstrap check
    coordinator.CursorOverrides[("TestPerspective", streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.None
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      snapshotStore: snapshotStore,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - BootstrapSnapshotAsync was called
    await Assert.That(runner.BootstrapCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(snapshotStore.HasAnyCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_NormalPath_WithSnapshotStore_HasSnapshots_SkipsBootstrapAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);
    var snapshotStore = new FakeSnapshotStore { HasSnapshots = true };

    var streamId = Guid.CreateVersion7();
    var lastEventId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = lastEventId,
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.None,
        WorkId = Guid.CreateVersion7()
      }
    ];

    coordinator.CursorOverrides[("TestPerspective", streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.None
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      snapshotStore: snapshotStore,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - BootstrapSnapshotAsync was NOT called (snapshots already exist)
    await Assert.That(runner.BootstrapCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task Worker_NormalPath_NullLastProcessedEventId_SkipsBootstrapAsync() {
    // Arrange - brand new stream with no cursor position
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);
    var snapshotStore = new FakeSnapshotStore { HasSnapshots = false };

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null, // Brand new stream
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.None,
        WorkId = Guid.CreateVersion7()
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      snapshotStore: snapshotStore,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - No bootstrap for brand new streams (no lastProcessedEventId)
    await Assert.That(runner.BootstrapCallCount).IsEqualTo(0);
    await Assert.That(snapshotStore.HasAnyCallCount).IsEqualTo(0);
  }

  #endregion

  #region Event Completion Tests

  [Test]
  public async Task Worker_CollectsWorkIds_InPerspectiveEventCompletionsAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator { CaptureRequests = true };
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new TrackingPerspectiveRunner();
    var registry = new SingleRunnerRegistry("TestPerspective", runner);

    var workId1 = Guid.CreateVersion7();
    var workId2 = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1,
        WorkId = workId1
      },
      new PerspectiveWork {
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1,
        WorkId = workId2
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(600);
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Event work IDs flow through the completion channel (channel architecture
    // replaces the legacy CapturedRequests.PerspectiveEventCompletions assertion).
    await Assert.That(harness.CompletionCapture.EventWorkIds.Count).IsGreaterThanOrEqualTo(1)
      .Because("Worker should enqueue completed event work IDs to the perspective completion channel");
  }

  #endregion

  #region Rewind Error Isolation Tests

  [Test]
  public async Task Worker_RewindFails_LogsErrorAndContinuesAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var runner = new ThrowingPerspectiveRunner(new InvalidOperationException("Snapshot corrupt"));
    var registry = new SingleRunnerRegistry("TestPerspective", runner);

    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Status = PerspectiveProcessingStatus.RewindRequired,
        WorkId = Guid.CreateVersion7()
      }
    ];

    coordinator.CursorOverrides[("TestPerspective", streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act — worker should NOT crash despite the rewind throwing
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    foreach (var __w in coordinator.PerspectiveWorkToReturn) {
      await harness.EnqueueWorkAsync(__w, cts.Token);
    }
    await Task.Delay(400);
    await cts.CancelAsync();

    // Assert — worker should stop cleanly (no unhandled exception)
    Exception? caught = null;
    try { await workerTask; } catch (Exception ex) when (ex is not OperationCanceledException) { caught = ex; }
    await Assert.That(caught).IsNull()
      .Because("A rewind failure should NOT crash the worker");
    await Assert.That(runner.RewindAndRunCallCount).IsGreaterThanOrEqualTo(1)
      .Because("RewindAndRunAsync should have been called");
  }

  #endregion

  #region Test Doubles

  private sealed class ThrowingPerspectiveRunner(Exception exceptionToThrow) : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);
    public int RewindAndRunCallCount { get; private set; }

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      RewindAndRunCallCount++;
      throw exceptionToThrow;
    }

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class TrackingPerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object); // Fake — no real perspective type
    public int RunCallCount { get; private set; }
    public int RewindAndRunCallCount { get; private set; }
    public int BootstrapCallCount { get; private set; }
    public Guid? LastTriggeringEventId { get; private set; }

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      RunCallCount++;
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      RewindAndRunCallCount++;
      LastTriggeringEventId = triggeringEventId;
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      BootstrapCallCount++;
      return Task.CompletedTask;
    }
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];
    public int ClaimWorkCallCount { get; private set; }
    public bool CaptureRequests { get; set; }
    public List<ClaimWorkRequest> CapturedRequests { get; } = [];
    public Dictionary<(string PerspectiveName, Guid StreamId), PerspectiveCursorInfo> CursorOverrides { get; } = [];

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      ClaimWorkCallCount++;
      if (CaptureRequests) {
        CapturedRequests.Add(request);
      }

      var work = new List<PerspectiveWork>(PerspectiveWorkToReturn);
      PerspectiveWorkToReturn.Clear();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (CursorOverrides.TryGetValue((perspectiveName, streamId), out var cursor)) {
        return Task.FromResult<PerspectiveCursorInfo?>(cursor);
      }
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
  private sealed class SingleRunnerRegistry(string perspectiveName, IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) =>
      name == perspectiveName ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(perspectiveName, $"global::Test.{perspectiveName}", "global::Test.TestModel", ["global::Test.TestEvent"])];

    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class FakePerspectiveStreamLocker : IPerspectiveStreamLocker {
    public bool AcquireResult { get; set; } = true;
    public int AcquireCallCount { get; private set; }
    public int RenewCallCount { get; private set; }
    public int ReleaseCallCount { get; private set; }
    public string? LastReason { get; private set; }

    public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) {
      AcquireCallCount++;
      LastReason = reason;
      return Task.FromResult(AcquireResult);
    }

    public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      RenewCallCount++;
      return Task.CompletedTask;
    }

    public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      ReleaseCallCount++;
      return Task.CompletedTask;
    }
  }

  private sealed class FakeSnapshotStore : IPerspectiveSnapshotStore {
    public bool HasSnapshots { get; set; }
    public int HasAnyCallCount { get; private set; }

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, System.Text.Json.JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<(Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, System.Text.Json.JsonDocument)?>(null);

    public Task<(Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, System.Text.Json.JsonDocument)?>(null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
      HasAnyCallCount++;
      return Task.FromResult(HasSnapshots);
    }

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  #endregion
}
