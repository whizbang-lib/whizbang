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
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Tests for WorkCoordinatorPublisherWorker database readiness integration.
/// Phase 3B: Verifies that database readiness checks prevent work coordinator calls until database is available.
/// Uses proper synchronization primitives (TaskCompletionSource, SemaphoreSlim) instead of polling.
/// </summary>
public class WorkCoordinatorPublisherWorkerDatabaseReadinessTests {
  private sealed record _testMessage { }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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

    // Wait for at least one readiness check to complete so we know the worker loop ran
    await databaseReadinessCheck.WaitForCheckAsync(TimeSpan.FromSeconds(5));

    // Assert - ProcessWorkBatchAsync should NOT be called when database not ready
    // Give a short window to confirm no call was made after the check ran
    await Task.Delay(100);

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

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

    // Wait for ProcessWorkBatchAsync to be called (signal-based, not polling)
    await testWorkCoordinator.WaitForCallAsync(TimeSpan.FromSeconds(2));

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

    // Wait for at least one readiness check to complete
    await databaseReadinessCheck.WaitForCheckAsync(TimeSpan.FromSeconds(5));

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
    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Wait for the specific "Database not ready for X consecutive polling cycles" warning
    // This only happens after the threshold (10 consecutive checks) is exceeded
    await testLogger.WaitForLogContainingAsync("Database not ready for", TimeSpan.FromSeconds(30));

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - LogWarning should mention consecutive polling cycles
    var dbWarnings = testLogger.GetLogsContaining("Database not ready for");
    await Assert.That(dbWarnings.Count).IsGreaterThanOrEqualTo(1)
      .Because("Warning should mention database readiness issue with consecutive count");
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

    // Wait for some not-ready checks to accumulate
    await databaseReadinessCheck.WaitForCheckAsync(TimeSpan.FromSeconds(5));

    // Act - Database becomes ready
    databaseReadinessCheck.IsReadyResult = true;

    // Wait for ProcessWorkBatchAsync to be called (proves database became ready and counter reset)
    await testWorkCoordinator.WaitForCallAsync(TimeSpan.FromSeconds(2));

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Counter should reset to 0 after database becomes ready
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsEqualTo(0)
      .Because("Consecutive counter should reset when database becomes ready");
  }

  [Test]
  public async Task DatabaseNotReady_MessagesBuffered_UntilReadyAsync() {
    // Arrange
    var messageId = Guid.CreateVersion7();
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

    // Database not ready - wait for at least one check, then verify no work published
    await databaseReadinessCheck.WaitForCheckAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100); // Brief window to confirm nothing was published

    await Assert.That(publishStrategy.PublishedWork.Count).IsEqualTo(0)
      .Because("No work should be published when database not ready");

    // Act - Database becomes ready
    databaseReadinessCheck.IsReadyResult = true;
    testWorkCoordinator.WorkToReturn = [_createTestOutboxWork(messageId)];

    // Wait for work to be published (signal-based)
    await publishStrategy.WaitForPublishAsync(TimeSpan.FromSeconds(5));

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Work should be published after database becomes ready
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Work should be published once database becomes ready");
  }

  // Test helper classes
  private sealed class TestWorkCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _callSignal = new(0, int.MaxValue);

    public void Dispose() => _callSignal.Dispose();
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public int CallCount { get; private set; }

    /// <summary>
    /// Waits for at least one call to ProcessWorkBatchAsync.
    /// </summary>
    public async Task WaitForCallAsync(TimeSpan timeout) {
      if (!await _callSignal.WaitAsync(timeout)) {
        throw new TimeoutException("ProcessWorkBatchAsync was not called within timeout");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {

      CallCount++;
      _callSignal.Release();
      var work = new List<OutboxWork>(WorkToReturn);
      WorkToReturn.Clear();  // Return work once, then empty

      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class TestDatabaseReadinessCheck : IDatabaseReadinessCheck, IDisposable {
    private volatile bool _isReadyResult = true;
    private readonly SemaphoreSlim _checkSignal = new(0, int.MaxValue);

    public void Dispose() => _checkSignal.Dispose();

    public event Action? OnReadinessChanged;

    public bool IsReadyResult {
      get => _isReadyResult;
      set {
        _isReadyResult = value;
        OnReadinessChanged?.Invoke();
      }
    }

    /// <summary>
    /// Waits for at least one call to IsReadyAsync.
    /// </summary>
    public async Task WaitForCheckAsync(TimeSpan timeout) {
      if (!await _checkSignal.WaitAsync(timeout)) {
        throw new TimeoutException("IsReadyAsync was not called within timeout");
      }
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      _checkSignal.Release();
      return Task.FromResult(_isReadyResult);
    }
  }

  private sealed class TestPublishStrategy : IMessagePublishStrategy, IDisposable {
    private readonly SemaphoreSlim _publishSignal = new(0, int.MaxValue);

    public void Dispose() => _publishSignal.Dispose();
    public List<OutboxWork> PublishedWork { get; } = [];

    /// <summary>
    /// Waits for at least one call to PublishAsync.
    /// </summary>
    public async Task WaitForPublishAsync(TimeSpan timeout) {
      if (!await _publishSignal.WaitAsync(timeout)) {
        throw new TimeoutException("PublishAsync was not called within timeout");
      }
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);  // Transport always ready for these tests
    }

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishedWork.Add(work);
      _publishSignal.Release();
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published
      });
    }
  }

  private sealed class TestLogger<T> : ILogger<T>, IDisposable {
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _logs = new();
    private readonly SemaphoreSlim _logSignal = new(0, int.MaxValue);

    public void Dispose() => _logSignal.Dispose();

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      _logs.Enqueue(new LogEntry {
        LogLevel = logLevel,
        Message = formatter(state, exception),
        Exception = exception
      });
      _logSignal.Release();
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>
    /// Waits for a log message containing the specified text to appear.
    /// </summary>
    public async Task WaitForLogContainingAsync(string text, TimeSpan timeout) {
      var deadline = DateTime.UtcNow + timeout;
      while (DateTime.UtcNow < deadline) {
        if (_logs.Any(l => l.Message.Contains(text, StringComparison.OrdinalIgnoreCase))) {
          return;
        }
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) {
          break;
        }
        var waitTime = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
        if (!await _logSignal.WaitAsync(waitTime)) {
          // Timeout on semaphore, check condition again
          continue;
        }
      }
      if (!_logs.Any(l => l.Message.Contains(text, StringComparison.OrdinalIgnoreCase))) {
        throw new TimeoutException($"Log message containing '{text}' was not found within {timeout}");
      }
    }

    public List<LogEntry> GetLogsContaining(string text) =>
      [.. _logs.Where(l => l.Message.Contains(text, StringComparison.OrdinalIgnoreCase))];

    public List<LogEntry> GetLogsAtLevel(LogLevel level) =>
      [.. _logs.Where(l => l.LogLevel == level)];

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
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
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
    public void ClearInFlight() { }
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

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }
}
