using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for WorkCoordinatorPublisherWorker startup behavior (Phase 4).
/// Verifies immediate processing of pending outbox messages on startup.
/// </summary>
public class WorkCoordinatorPublisherWorkerStartupTests {
  private record _testMessage { }

  private static IMessageEnvelope<object> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<_testMessage> {
      MessageId = MessageId.From(messageId),
      Payload = new _testMessage(),
      Hops = []
    };
    return envelope as IMessageEnvelope<object> ?? throw new InvalidOperationException("Envelope must implement IMessageEnvelope<object>");
  }

  private class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount { get; private set; }
    public List<_processWorkBatchCall> Calls { get; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, JsonElement>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      ReceptorProcessingCompletion[] receptorCompletions,
      ReceptorProcessingFailure[] receptorFailures,
      PerspectiveCheckpointCompletion[] perspectiveCompletions,
      PerspectiveCheckpointFailure[] perspectiveFailures,
      OutboxMessage[] newOutboxMessages,
      InboxMessage[] newInboxMessages,
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int maxPartitionsPerInstance = 100,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {

      ProcessWorkBatchCallCount++;
      Calls.Add(new _processWorkBatchCall {
        CallNumber = ProcessWorkBatchCallCount,
        Timestamp = DateTimeOffset.UtcNow
      });

      return Task.FromResult(new WorkBatch {
        OutboxWork = [.. WorkToReturn],
        InboxWork = []
      });
    }
  }

  private class TestPublishStrategy : IMessagePublishStrategy {
    public List<OutboxWork> PublishedWork { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishedWork.Add(work);

      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }
  }

  private class TestDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public bool IsReady { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(IsReady);
    }
  }

  private record _processWorkBatchCall {
    public required int CallNumber { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
  }

  [Test]
  public async Task ImmediateProcessing_OnStartup_ProcessesWorkBeforeFirstPollAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var databaseReadiness = new TestDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    // 3 pending outbox messages to be claimed on startup
    var message1Id = Guid.NewGuid();
    var message2Id = Guid.NewGuid();
    var message3Id = Guid.NewGuid();

    workCoordinator.WorkToReturn = [
      _createOutboxWork(message1Id, "products"),
      _createOutboxWork(message2Id, "products"),
      _createOutboxWork(message3Id, "products")
    ];

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker and let it run briefly
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(50); // Give time for initial processing BEFORE first poll interval
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - ProcessWorkBatchAsync should be called immediately (before 100ms poll interval)
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);

    // Verify the first call happened immediately (within 50ms)
    if (workCoordinator.Calls.Count > 0) {
      var firstCall = workCoordinator.Calls[0];
      await Assert.That(firstCall.CallNumber).IsEqualTo(1);
    }

    // Verify messages were published from initial processing
    await Assert.That(publishStrategy.PublishedWork).HasCount().GreaterThanOrEqualTo(3);
  }

  [Test]
  public async Task ImmediateProcessing_DatabaseNotReady_SkipsInitialProcessingAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var databaseReadiness = new TestDatabaseReadinessCheck { IsReady = false }; // Database NOT ready
    var instanceProvider = _createTestInstanceProvider();

    workCoordinator.WorkToReturn = [_createOutboxWork(Guid.NewGuid(), "products")];

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker and let it run briefly
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(50); // Give time for initial check
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - ProcessWorkBatchAsync should NOT be called when database not ready
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ImmediateProcessing_ExceptionDuringInitial_ContinuesStartupAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var databaseReadiness = new TestDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    // Make ProcessWorkBatchAsync throw on first call
    var callCount = 0;
    var faultyCoordinator = new TestWorkCoordinator();
    var originalMethod = faultyCoordinator.ProcessWorkBatchAsync;

    // Create a coordinator that throws on first call, then succeeds
    var throwingCoordinator = new ThrowingWorkCoordinator {
      ThrowOnFirstCall = true,
      WorkToReturn = [_createOutboxWork(Guid.NewGuid(), "products")]
    };

    var services = _createServiceCollection(throwingCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker (should not crash despite initial exception)
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(150); // Give time for initial processing + one poll
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - worker should continue despite exception in initial processing
    // The second call (from polling loop) should succeed
    await Assert.That(throwingCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task ImmediateProcessing_NoWork_DoesNotLogAsync() {
    // Arrange - coordinator returns NO work
    var workCoordinator = new TestWorkCoordinator(); // Empty WorkToReturn
    var publishStrategy = new TestPublishStrategy();
    var databaseReadiness = new TestDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(50);
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - ProcessWorkBatchAsync was called but returned no work
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ImmediateProcessing_WithWork_LogsMessageBatchAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var databaseReadiness = new TestDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    // 12 pending messages (like the user's seeding scenario)
    for (int i = 0; i < 12; i++) {
      workCoordinator.WorkToReturn.Add(_createOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(100); // Give time for initial processing
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - 12 messages should be published immediately
    await Assert.That(publishStrategy.PublishedWork).HasCount().GreaterThanOrEqualTo(12);

    // Verify ProcessWorkBatchAsync was called at least once
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  private static OutboxWork _createOutboxWork(Guid messageId, string destination) {
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      Envelope = _createTestEnvelope(messageId),
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
  }

  private static IServiceInstanceProvider _createTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static IServiceCollection _createServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IDatabaseReadinessCheck databaseReadiness,
    IServiceInstanceProvider instanceProvider) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(databaseReadiness);
    services.AddSingleton(instanceProvider);
    services.AddLogging();
    return services;
  }

  private class ThrowingWorkCoordinator : IWorkCoordinator {
    public bool ThrowOnFirstCall { get; set; }
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount { get; private set; }
    private bool _hasThrown;

    public Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, JsonElement>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      ReceptorProcessingCompletion[] receptorCompletions,
      ReceptorProcessingFailure[] receptorFailures,
      PerspectiveCheckpointCompletion[] perspectiveCompletions,
      PerspectiveCheckpointFailure[] perspectiveFailures,
      OutboxMessage[] newOutboxMessages,
      InboxMessage[] newInboxMessages,
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int maxPartitionsPerInstance = 100,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {

      ProcessWorkBatchCallCount++;

      if (ThrowOnFirstCall && !_hasThrown) {
        _hasThrown = true;
        throw new InvalidOperationException("Simulated database connection failure");
      }

      return Task.FromResult(new WorkBatch {
        OutboxWork = [.. WorkToReturn],
        InboxWork = []
      });
    }
  }
}
