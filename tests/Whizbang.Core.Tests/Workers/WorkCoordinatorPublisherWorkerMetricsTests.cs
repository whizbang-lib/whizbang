using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for WorkCoordinatorPublisherWorker observability metrics.
/// Phase 2: Verifies that transport readiness buffering is properly tracked and logged.
/// </summary>
public class WorkCoordinatorPublisherWorkerMetricsTests {
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
  public async Task TransportNotReady_SingleBuffer_LogsInformationAsync() {
    // Arrange
    var testLogger = new TestLogger<WorkCoordinatorPublisherWorker>();
    var workCoordinator = new TestWorkCoordinator {
      WorkToReturn = [
        _createTestOutboxWork(Guid.CreateVersion7())
      ]
    };
    var publishStrategy = new TestPublishStrategy {
      IsReadyResult = false  // Transport not ready
    };
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider, testLogger);

    // Act - Start worker and wait for the expected log message via signal
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await testLogger.WaitForLogContainingAsync("Transport not ready, buffering message", TimeSpan.FromSeconds(10));
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - LogInformation should be emitted for transport buffering
    var bufferLogs = testLogger.GetLogsContaining("Transport not ready, buffering message");
    await Assert.That(bufferLogs.Count).IsGreaterThanOrEqualTo(1)
      .Because("Transport buffering should log at Information level");
    await Assert.That(bufferLogs[0].LogLevel).IsEqualTo(LogLevel.Information)
      .Because("Buffering is important operational info, not debug");
  }

  [Test]
  public async Task TransportNotReady_ConsecutiveBuffers_TracksCountAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator {
      WorkToReturn = [
        _createTestOutboxWork(Guid.CreateVersion7()),
        _createTestOutboxWork(Guid.CreateVersion7()),
        _createTestOutboxWork(Guid.CreateVersion7())
      ]
    };
    var publishStrategy = new TestPublishStrategy {
      IsReadyResult = false  // Transport stays not ready
    };
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act
    var worker = (WorkCoordinatorPublisherWorker)services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await workCoordinator.WaitForFirstCallAsync(TimeSpan.FromSeconds(10));

    // Wait for the worker loop to complete the not-ready check and increment the counter
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (worker.ConsecutiveNotReadyChecks < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(10, CancellationToken.None);
    }

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Metrics should track consecutive not-ready checks
    await Assert.That(worker.ConsecutiveNotReadyChecks).IsGreaterThanOrEqualTo(1)
      .Because("Each transport not-ready check should increment counter");
  }

  [Test]
  public async Task TransportNotReady_ExceedsThreshold_LogsWarningAsync() {
    // Arrange
    var testLogger = new TestLogger<WorkCoordinatorPublisherWorker>();
    var messageIds = new List<Guid>();
    for (int i = 0; i < 12; i++) {  // Create 12 messages to exceed threshold of 10
      messageIds.Add(Guid.CreateVersion7());
    }
    var workCoordinator = new TestWorkCoordinator {
      WorkToReturn = messageIds.ConvertAll(_createTestOutboxWork)
    };
    var publishStrategy = new TestPublishStrategy {
      IsReadyResult = false  // Transport never ready
    };
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider, testLogger);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await testLogger.WaitForLogContainingAsync("Transport not ready for", TimeSpan.FromSeconds(10));
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - LogWarning should be emitted after 10 consecutive not-ready
    var warningLogs = testLogger.GetLogsAtLevel(LogLevel.Warning);
    await Assert.That(warningLogs.Count).IsGreaterThanOrEqualTo(1)
      .Because("After 10 consecutive not-ready checks, a warning should be logged");

    var transportWarnings = testLogger.GetLogsContaining("Transport not ready for");
    await Assert.That(transportWarnings.Count).IsGreaterThanOrEqualTo(1)
      .Because("Warning should mention transport readiness issue");
  }

  [Test]
  public async Task TransportBecomesReady_ResetsConsecutiveCounterAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);
    var worker = (WorkCoordinatorPublisherWorker)services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();

    // Simulate 5 not-ready messages
    publishStrategy.IsReadyResult = false;
    workCoordinator.WorkToReturn = [
      _createTestOutboxWork(Guid.CreateVersion7()),
      _createTestOutboxWork(Guid.CreateVersion7()),
      _createTestOutboxWork(Guid.CreateVersion7()),
      _createTestOutboxWork(Guid.CreateVersion7()),
      _createTestOutboxWork(Guid.CreateVersion7())
    ];

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await workCoordinator.WaitForFirstCallAsync(TimeSpan.FromSeconds(10));

    // Act - Transport becomes ready
    publishStrategy.IsReadyResult = true;
    workCoordinator.WorkToReturn = [_createTestOutboxWork(Guid.CreateVersion7())];
    await publishStrategy.WaitForPublishAsync(TimeSpan.FromSeconds(10));

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - Counter should reset to 0 after successful publish
    await Assert.That(worker.ConsecutiveNotReadyChecks).IsEqualTo(0)
      .Because("Consecutive counter should reset when transport becomes ready");
  }

  [Test]
  public async Task BufferedMessages_TracksCountAsync() {
    // Arrange
    var messageIds = new List<Guid> {
      Guid.CreateVersion7(),
      Guid.CreateVersion7(),
      Guid.CreateVersion7()
    };
    var workCoordinator = new TestWorkCoordinator {
      WorkToReturn = messageIds.ConvertAll(_createTestOutboxWork)
    };
    var publishStrategy = new TestPublishStrategy {
      IsReadyResult = false  // Transport not ready
    };
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act
    var worker = (WorkCoordinatorPublisherWorker)services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wait for 2nd call — by then the first batch is fully processed (buffered + lease renewed)
    await workCoordinator.WaitForCallCountAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - BufferedMessageCount should reflect lease renewals
    await Assert.That(worker.BufferedMessageCount).IsGreaterThanOrEqualTo(3)
      .Because("All 3 messages should be buffered due to transport not ready");
  }

  [Test]
  public async Task LeaseRenewals_TrackedInMetricsAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator {
      WorkToReturn = [
        _createTestOutboxWork(Guid.CreateVersion7()),
        _createTestOutboxWork(Guid.CreateVersion7())
      ]
    };
    var publishStrategy = new TestPublishStrategy {
      IsReadyResult = false
    };
    var instanceProvider = _createTestInstanceProvider();
    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act
    var worker = (WorkCoordinatorPublisherWorker)services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wait for 2nd call — by then the first batch is fully processed (buffered + lease renewed)
    await workCoordinator.WaitForCallCountAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - TotalLeaseRenewals should accumulate across batches
    await Assert.That(worker.TotalLeaseRenewals).IsGreaterThanOrEqualTo(2)
      .Because("Each buffered message should contribute to total lease renewals");
  }

  // Test helper classes
  private sealed class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public int CallCount { get; private set; }
    private readonly TaskCompletionSource _firstCallSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _callCountLock = new();
    private readonly List<(int TargetCount, TaskCompletionSource Signal)> _callCountWaiters = [];

    public Task WaitForFirstCallAsync(TimeSpan timeout) => _firstCallSignal.Task.WaitAsync(timeout);

    /// <summary>
    /// Returns a task that completes when CallCount reaches or exceeds the specified count.
    /// </summary>
    public Task WaitForCallCountAsync(int count, TimeSpan timeout) {
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_callCountLock) {
        if (CallCount >= count) {
          tcs.TrySetResult();
          return tcs.Task;
        }
        _callCountWaiters.Add((count, tcs));
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {

      CallCount++;
      var work = new List<OutboxWork>(WorkToReturn);
      WorkToReturn.Clear();  // Return work once, then empty
      _firstCallSignal.TrySetResult();

      lock (_callCountLock) {
        for (int i = _callCountWaiters.Count - 1; i >= 0; i--) {
          if (CallCount >= _callCountWaiters[i].TargetCount) {
            _callCountWaiters[i].Signal.TrySetResult();
            _callCountWaiters.RemoveAt(i);
          }
        }
      }

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

  private sealed class TestPublishStrategy : IMessagePublishStrategy, IDisposable {
    public bool IsReadyResult { get; set; } = true;
    public List<OutboxWork> PublishedWork { get; } = [];
    private readonly object _isReadyLock = new();
    private int _isReadyCheckCount;
    private readonly List<(int TargetCount, TaskCompletionSource Signal)> _isReadyWaiters = [];
    private readonly SemaphoreSlim _publishSignal = new(0);

    /// <summary>
    /// Returns a task that completes when IsReadyAsync has been called at least the specified number of times.
    /// </summary>
    public Task WaitForIsReadyCheckCountAsync(int count, TimeSpan timeout) {
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_isReadyLock) {
        if (_isReadyCheckCount >= count) {
          tcs.TrySetResult();
          return tcs.Task;
        }
        _isReadyWaiters.Add((count, tcs));
      }
      return tcs.Task.WaitAsync(timeout);
    }

    /// <summary>
    /// Returns a task that completes when PublishAsync has been called at least once (since last wait).
    /// </summary>
    public async Task WaitForPublishAsync(TimeSpan timeout) => await _publishSignal.WaitAsync(timeout);
    public void Dispose() => _publishSignal.Dispose();

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      lock (_isReadyLock) {
        _isReadyCheckCount++;
        for (int i = _isReadyWaiters.Count - 1; i >= 0; i--) {
          if (_isReadyCheckCount >= _isReadyWaiters[i].TargetCount) {
            _isReadyWaiters[i].Signal.TrySetResult();
            _isReadyWaiters.RemoveAt(i);
          }
        }
      }
      return Task.FromResult(IsReadyResult);
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

  private sealed class TestLogger<T> : ILogger<T> {
    private readonly List<LogEntry> _logs = [];
    private readonly List<(string Substring, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>
    /// Returns a task that completes when a log message containing the specified substring is written.
    /// </summary>
    public Task WaitForLogContainingAsync(string substring, TimeSpan timeout) {
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_logs) {
        // Check if already logged
        if (_logs.Exists(l => l.Message.Contains(substring, StringComparison.OrdinalIgnoreCase))) {
          tcs.TrySetResult();
          return tcs.Task;
        }
        _waiters.Add((substring, tcs));
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var entry = new LogEntry {
        LogLevel = logLevel,
        Message = formatter(state, exception),
        Exception = exception
      };
      lock (_logs) {
        _logs.Add(entry);
        for (int i = _waiters.Count - 1; i >= 0; i--) {
          if (entry.Message.Contains(_waiters[i].Substring, StringComparison.OrdinalIgnoreCase)) {
            _waiters[i].Signal.TrySetResult();
            _waiters.RemoveAt(i);
          }
        }
      }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public List<LogEntry> GetLogsContaining(string text) {
      lock (_logs) { return _logs.FindAll(l => l.Message.Contains(text, StringComparison.OrdinalIgnoreCase)); }
    }

    public List<LogEntry> GetLogsAtLevel(LogLevel level) {
      lock (_logs) { return _logs.FindAll(l => l.LogLevel == level); }
    }

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
    ILogger<WorkCoordinatorPublisherWorker>? logger = null) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
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
  }
}
