using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for WorkCoordinatorPublisherWorker database readiness integration.
/// Phase 3B: Verifies that database readiness checks prevent work coordinator calls until database is available.
/// </summary>
public class WorkCoordinatorPublisherWorkerDatabaseReadinessTests {
  private sealed record _testMessage { }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
    return envelope;
  }

  [Test]
  public async Task DatabaseNotReady_ProcessWorkBatchAsync_SkippedAsync() {
    // Arrange
    var testLogger = new TestLogger<WorkCoordinatorPublisherWorker>();
    var testWorkCoordinator = new TestWorkCoordinator();
    var databaseReadinessCheck = new TestDatabaseReadinessCheck {
      IsReadyResult = false  // Database not ready
    };
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(
      testWorkCoordinator,
      publishStrategy,
      instanceProvider,
      databaseReadinessCheck,
      testLogger
    );

    // Act - Start worker briefly
    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(300);  // Allow time for polling
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - ProcessWorkBatchAsync should NOT be called
    await Assert.That(testWorkCoordinator.CallCount).IsEqualTo(0)
      .Because("ProcessWorkBatchAsync should be skipped when database not ready");

    // Verify logging
    var dbLogs = testLogger.GetLogsContaining("Database not ready");
    await Assert.That(dbLogs.Count).IsGreaterThanOrEqualTo(1)
      .Because("Database unavailability should be logged");
  }

  [Test]
  public async Task DatabaseReady_ProcessWorkBatchAsync_CalledAsync() {
    // Arrange
    var testWorkCoordinator = new TestWorkCoordinator();
    var databaseReadinessCheck = new TestDatabaseReadinessCheck {
      IsReadyResult = true  // Database ready
    };
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(
      testWorkCoordinator,
      publishStrategy,
      instanceProvider,
      databaseReadinessCheck
    );

    // Act
    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(300);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - ProcessWorkBatchAsync should be called
    await Assert.That(testWorkCoordinator.CallCount).IsGreaterThanOrEqualTo(1)
      .Because("ProcessWorkBatchAsync should be called when database is ready");
  }

  [Test]
  public async Task DatabaseNotReady_ConsecutiveChecks_TracksCountAsync() {
    // Arrange
    var testWorkCoordinator = new TestWorkCoordinator();
    var databaseReadinessCheck = new TestDatabaseReadinessCheck {
      IsReadyResult = false  // Database stays not ready
    };
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(
      testWorkCoordinator,
      publishStrategy,
      instanceProvider,
      databaseReadinessCheck
    );

    // Act
    var worker = (WorkCoordinatorPublisherWorker)services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(500);  // Allow multiple polling cycles
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Consecutive not-ready checks should be tracked
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThanOrEqualTo(1)
      .Because("Each database not-ready check should increment counter");
  }

  [Test]
  public async Task DatabaseNotReady_ExceedsThreshold_LogsWarningAsync() {
    // Arrange
    var testLogger = new TestLogger<WorkCoordinatorPublisherWorker>();
    var testWorkCoordinator = new TestWorkCoordinator();
    var databaseReadinessCheck = new TestDatabaseReadinessCheck {
      IsReadyResult = false  // Database never ready
    };
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(
      testWorkCoordinator,
      publishStrategy,
      instanceProvider,
      databaseReadinessCheck,
      testLogger
    );

    // Act - Run long enough to exceed threshold (10 consecutive checks)
    // Increased wait time for systems under load
    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(2500);  // Allow sufficient time for multiple polls under load
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - LogWarning should be emitted after 10 consecutive not-ready
    var warningLogs = testLogger.GetLogsAtLevel(LogLevel.Warning);
    await Assert.That(warningLogs.Count).IsGreaterThanOrEqualTo(1)
      .Because("After 10 consecutive not-ready checks, a warning should be logged");

    var dbWarnings = testLogger.GetLogsContaining("Database not ready for");
    await Assert.That(dbWarnings.Count).IsGreaterThanOrEqualTo(1)
      .Because("Warning should mention database readiness issue");
  }

  [Test]
  public async Task DatabaseBecomesReady_ResetsConsecutiveCounterAsync() {
    // Arrange
    var testWorkCoordinator = new TestWorkCoordinator();
    var databaseReadinessCheck = new TestDatabaseReadinessCheck {
      IsReadyResult = false  // Start not ready
    };
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(
      testWorkCoordinator,
      publishStrategy,
      instanceProvider,
      databaseReadinessCheck
    );

    var worker = (WorkCoordinatorPublisherWorker)services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Wait for some not-ready checks
    await Task.Delay(300);

    // Act - Database becomes ready
    databaseReadinessCheck.IsReadyResult = true;
    await Task.Delay(300);

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Counter should reset to 0 after database becomes ready
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsEqualTo(0)
      .Because("Consecutive counter should reset when database becomes ready");
  }

  [Test]
  public async Task DatabaseNotReady_MessagesBuffered_UntilReadyAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var testWorkCoordinator = new TestWorkCoordinator {
      WorkToReturn = [_createTestOutboxWork(messageId)]
    };
    var databaseReadinessCheck = new TestDatabaseReadinessCheck {
      IsReadyResult = false  // Start not ready
    };
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(
      testWorkCoordinator,
      publishStrategy,
      instanceProvider,
      databaseReadinessCheck
    );

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Database not ready - ProcessWorkBatchAsync skipped
    await Task.Delay(300);
    await Assert.That(publishStrategy.PublishedWork).IsEmpty()
      .Because("No work should be published when database not ready");

    // Act - Database becomes ready
    databaseReadinessCheck.IsReadyResult = true;
    testWorkCoordinator.WorkToReturn = [_createTestOutboxWork(messageId)];
    await Task.Delay(300);

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Work should be published after database becomes ready
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Work should be published once database becomes ready");
  }

  // Test helper classes
  private sealed class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public int CallCount { get; private set; }

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
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {

      CallCount++;
      var work = new List<OutboxWork>(WorkToReturn);
      WorkToReturn.Clear();  // Return work once, then empty

      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  private sealed class TestDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public bool IsReadyResult { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(IsReadyResult);
    }
  }

  private sealed class TestPublishStrategy : IMessagePublishStrategy {
    public List<OutboxWork> PublishedWork { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);  // Transport always ready for these tests
    }

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishedWork.Add(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published
      });
    }
  }

  private sealed class TestLogger<T> : ILogger<T> {
    private readonly List<LogEntry> _logs = [];

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      _logs.Add(new LogEntry {
        LogLevel = logLevel,
        Message = formatter(state, exception),
        Exception = exception
      });
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public List<LogEntry> GetLogsContaining(string text) =>
      _logs.FindAll(l => l.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    public List<LogEntry> GetLogsAtLevel(LogLevel level) =>
      _logs.FindAll(l => l.LogLevel == level);

    public sealed class LogEntry {
      public LogLevel LogLevel { get; init; }
      public string Message { get; init; } = "";
      public Exception? Exception { get; init; }
    }
  }

  private static OutboxWork _createTestOutboxWork(Guid messageId) {
    return new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
  }

  private static ServiceInstanceProvider _createTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static ServiceProvider _createServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IDatabaseReadinessCheck databaseReadinessCheck,
    ILogger<WorkCoordinatorPublisherWorker>? logger = null) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton(databaseReadinessCheck);
    services.AddSingleton<IWorkChannelWriter>(new TestWorkChannelWriter());  // Required by WorkCoordinatorPublisherWorker
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 100  // Fast polling for tests
    }));

    if (logger != null) {
      services.AddSingleton(logger);
    } else {
      services.AddLogging();
    }

    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  // Test helper - Mock work channel writer
  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    private readonly System.Threading.Channels.Channel<OutboxWork> _channel;
    public List<OutboxWork> WrittenWork { get; } = [];

    public TestWorkChannelWriter() {
      _channel = System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>();
    }

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return _channel.Writer.TryWrite(work);
    }

    public void Complete() {
      _channel.Writer.Complete();
    }
  }
}
