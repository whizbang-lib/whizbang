using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Services;

/// <summary>
/// The stranded-saga sweep runs as a maintenance step: after the schema is ready, once the service has
/// settled, on the maintenance interval. It asks the work coordinator which sagas still have a tick
/// coming and has every saga service arm the ones whose chain has ended.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class StrandedSagaSweepStepTests {

  private static readonly string _tickTypeName =
    EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.AssemblyQualifiedName(typeof(SagaCompletionWatchdogTickEvent)));

  private static (StrandedSagaSweepStep Step, IServiceProvider Services, LogSink Log) _build(
      IWorkCoordinator? coordinator, params ISagaWatchdogParticipant[] participants) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddSingleton(coordinator);
    }
    foreach (var p in participants) {
      services.AddSingleton(p);
    }
    var log = new LogSink();
    return (new StrandedSagaSweepStep(log), services.BuildServiceProvider(), log);
  }

  [Test]
  public async Task Step_AsksTheCoordinatorForPendingTicks_AndHandsItsAnswerToEachSagaAsync() {
    var waiting = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var stopped = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var coordinator = new PendingCoordinator(new HashSet<Guid> { waiting });
    var participant = new AskingParticipant("A", [waiting, stopped]);
    var (step, services, _) = _build(coordinator, participant);

    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(participant.Answer).IsNotNull();
    await Assert.That(participant.Answer!).IsEquivalentTo([waiting]);
    await Assert.That(coordinator.AskedStreams.Single()).IsEquivalentTo([waiting, stopped]);
    await Assert.That(coordinator.AskedTypes.Single()).IsEquivalentTo([_tickTypeName])
      .Because("only a watchdog tick is a wake; any other message on the saga's stream is not");
  }

  [Test]
  public async Task Step_WithNoWorkCoordinator_AnswersUnknownAsync() {
    var participant = new AskingParticipant("A", [Guid.Parse("00000000-0000-0000-0000-000000000003")]);
    var (step, services, _) = _build(null, participant);

    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(participant.Asked).IsTrue();
    await Assert.That(participant.Answer).IsNull()
      .Because("with nothing to ask, the sweep must act as if every saga had a tick coming");
  }

  [Test]
  public async Task Step_AFailingSaga_IsLogged_AndTheNextStillSweepsAsync() {
    var after = new AskingParticipant("After", []);
    var (step, services, log) = _build(new PendingCoordinator(new HashSet<Guid>()),
      new ThrowingParticipant("Broken", new InvalidOperationException("boom")), after);

    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(after.Asked).IsTrue()
      .Because("one saga service that cannot enumerate its sagas must not stop the sweep of the others");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("Broken", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Step_Canceled_PropagatesAsync() {
    var (step, services, _) = _build(new PendingCoordinator(new HashSet<Guid>()),
      new ThrowingParticipant("Canceled", new OperationCanceledException()));

    await Assert.That(async () => await step.RunAsync(services, CancellationToken.None))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Step_LogsTheTicksItArmed_ByNameAsync() {
    var (step, services, log) = _build(new PendingCoordinator(new HashSet<Guid>()), new ArmingParticipant("Import", 2));

    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information
      && e.Message.Contains("Import", StringComparison.Ordinal) && e.Message.Contains('2'))).IsTrue()
      .Because("an operator must see that stranded sagas were found and re-armed");
  }

  [Test]
  public async Task Step_ArmingNothing_LogsNothingAsync() {
    var (step, services, log) = _build(new PendingCoordinator(new HashSet<Guid>()), new ArmingParticipant("Import", 0));

    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(log.Entries).IsEmpty();
  }

  [Test]
  public async Task Step_IsNamedForLogsAsync() {
    var (step, _, _) = _build(null);

    await Assert.That(step.Name).IsEqualTo("stranded-saga-sweep");
  }

  [Test]
  public async Task AddWhizbangSagas_RegistersTheSweepAsAMaintenanceStepOnceAsync() {
    var services = new ServiceCollection();
    services.AddLogging();

    services.AddWhizbangSagas();
    services.AddWhizbangSagas();

    await Assert.That(services.Count(d => d.ServiceType == typeof(IMaintenanceStep)
      && d.ImplementationType == typeof(StrandedSagaSweepStep))).IsEqualTo(1)
      .Because("the maintenance cycle runs registered steps; registering twice would sweep twice");
  }

  // ── Test doubles ───────────────────────────────────────────────────────

  private sealed class AskingParticipant(string name, IReadOnlyList<Guid> ids) : ISagaWatchdogParticipant {
    public bool Asked { get; private set; }
    public IReadOnlySet<Guid>? Answer { get; private set; }
    public string SagaName => name;
    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken)
      => Task.FromResult(WatchdogTickOutcome.ReArmed);
    public async Task<int> ArmStrandedSagasAsync(ISagaWakeLookup wakes, CancellationToken cancellationToken) {
      Asked = true;
      Answer = await wakes.WithPendingWakeAsync(ids, cancellationToken);
      return 0;
    }
  }

  private sealed class ArmingParticipant(string name, int arms) : ISagaWatchdogParticipant {
    public string SagaName => name;
    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken)
      => Task.FromResult(WatchdogTickOutcome.ReArmed);
    public Task<int> ArmStrandedSagasAsync(ISagaWakeLookup wakes, CancellationToken cancellationToken) => Task.FromResult(arms);
  }

  private sealed class ThrowingParticipant(string name, Exception ex) : ISagaWatchdogParticipant {
    public string SagaName => name;
    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken)
      => Task.FromResult(WatchdogTickOutcome.ReArmed);
    public Task<int> ArmStrandedSagasAsync(ISagaWakeLookup wakes, CancellationToken cancellationToken) => Task.FromException<int>(ex);
  }

  private sealed class PendingCoordinator(IReadOnlySet<Guid> pending) : IWorkCoordinator {
    public List<IReadOnlyList<Guid>> AskedStreams { get; } = [];
    public List<IReadOnlyList<string>> AskedTypes { get; } = [];
    public Task<IReadOnlySet<Guid>?> GetStreamsWithPendingMessagesAsync(
        IReadOnlyList<Guid> streamIds, IReadOnlyList<string> messageTypeNames, CancellationToken cancellationToken = default) {
      AskedStreams.Add(streamIds);
      AskedTypes.Add(messageTypeNames);
      return Task.FromResult<IReadOnlySet<Guid>?>(pending);
    }
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  private sealed record LogEntry(LogLevel Level, string Message);

  private sealed class LogSink : ILogger<StrandedSagaSweepStep> {
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];
    public IReadOnlyList<LogEntry> Entries {
      get {
        lock (_lock) {
          return [.. _entries];
        }
      }
    }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
      }
    }
  }
}
