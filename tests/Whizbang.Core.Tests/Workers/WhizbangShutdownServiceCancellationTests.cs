using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// StopAsync runs on the shutdown path, where the host's token is ALREADY cancelled by the time
/// cleanup executes. Forwarding that token cancels the deregistration DELETE precisely when it
/// matters most, and letting the resulting OperationCanceledException escape turns a graceful stop
/// into a crash — the process exits non-zero, the orchestrator records a restart, and the instance
/// row is stranded in the fleet table with nothing in the log to say so.
/// </summary>
public class WhizbangShutdownServiceCancellationTests {

  [Test]
  public async Task StopAsync_WithAlreadyCancelledToken_DoesNotForwardCancellationToDeregistrationAsync() {
    var coordinator = new _RecordingCoordinator();
    var service = _build(coordinator, out _);
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await service.StopAsync(cts.Token);

    await Assert.That(coordinator.WasCalled).IsTrue();
    // The whole point: cleanup must run on a token that is NOT already cancelled, otherwise the
    // DELETE is cancelled mid-statement and the row survives.
    await Assert.That(coordinator.ReceivedCancelledToken).IsFalse();
  }

  [Test]
  public async Task StopAsync_WhenDeregistrationIsCancelled_DoesNotThrowAsync() {
    var coordinator = new _RecordingCoordinator { ThrowOnDeregister = new OperationCanceledException("Query was cancelled") };
    var service = _build(coordinator, out _);

    // Must not escape StopAsync — Host.StopAsync rethrows, which is what produces the crash exit.
    await service.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task StopAsync_WhenDeregistrationIsCancelled_LogsTheAbandonmentAsync() {
    var coordinator = new _RecordingCoordinator { ThrowOnDeregister = new OperationCanceledException("Query was cancelled") };
    var service = _build(coordinator, out var logger);

    await service.StopAsync(CancellationToken.None);

    // Swallowing silently would leave the row stranded with no trace at all — strictly worse than
    // crashing, because the leak becomes invisible. The swallow MUST be observable.
    await Assert.That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task StopAsync_WhenDeregistrationFaults_SwallowsAndLogsAsync() {
    var coordinator = new _RecordingCoordinator { ThrowOnDeregister = new InvalidOperationException("boom") };
    var service = _build(coordinator, out var logger);

    await service.StopAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task DeregistrationBudget_DefaultsToAValueThatCanReleaseARealBacklogAsync() {
    // Deregistration releases every lease the instance holds before deleting the row, in ONE
    // all-or-nothing call — so its cost scales with the claimed backlog, and a timeout releases
    // nothing at all. A budget of a few seconds cannot complete that on a backlogged service; it
    // just guarantees the leases stay held. The ceiling is the orchestrator's grace period
    // (commonly 30s), past which the process is hard-killed and the clean exit is lost.
    var options = new WhizbangCoreOptions();

    await Assert.That(options.ShutdownDeregistrationTimeout).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
    await Assert.That(options.ShutdownDeregistrationTimeout).IsLessThan(TimeSpan.FromSeconds(30));
  }

  private static WhizbangShutdownService _build(IWorkCoordinator coordinator, out _CapturingLogger logger) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    logger = new _CapturingLogger();
    return new WhizbangShutdownService(
      services.BuildServiceProvider(), new _StubInstanceProvider(), new WhizbangCoreOptions(), logger);
  }

  private sealed record _Entry(LogLevel Level, string Message);

  private sealed class _CapturingLogger : ILogger<WhizbangShutdownService> {
    public List<_Entry> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add(new _Entry(logLevel, formatter(state, exception)));
  }

  private sealed class _StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-service";
    public string HostName => "test-host";
    public int ProcessId => 1234;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>Records the token deregistration actually received, and can fail on demand.</summary>
  private sealed class _RecordingCoordinator : IWorkCoordinator {
    public bool WasCalled { get; private set; }
    public bool ReceivedCancelledToken { get; private set; }
    public Exception? ThrowOnDeregister { get; init; }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) {
      WasCalled = true;
      ReceivedCancelledToken = cancellationToken.IsCancellationRequested;
      return ThrowOnDeregister is not null ? Task.FromException(ThrowOnDeregister) : Task.CompletedTask;
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
