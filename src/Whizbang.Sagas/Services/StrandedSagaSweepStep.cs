using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Sagas.Services;

/// <summary>
/// The maintenance step that re-arms the completion watchdog of sagas whose chain has ended.
/// </summary>
/// <remarks>
/// <para>
/// A watchdog chain is armed once, when a saga starts, and each tick arms the next. A tick lost to an
/// instance that stopped, or delivered to a version where nothing received it, ends the chain, and the
/// saga stays in progress forever. This step finds such sagas and has their service arm one tick.
/// </para>
/// <para>
/// A maintenance step rather than a timer of its own, so it runs only once the schema is ready and the
/// service has settled, on the interval operators already configure for housekeeping. It asks the work
/// coordinator which sagas still have a tick waiting in the outbox or an inbox; those are left alone.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepStepTests.cs</tests>
public sealed partial class StrandedSagaSweepStep(ILogger<StrandedSagaSweepStep> logger) : IMaintenanceStep {

  private static readonly string[] _tickTypeNames = [
    EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.AssemblyQualifiedName(typeof(SagaCompletionWatchdogTickEvent))),
  ];

  /// <inheritdoc />
  public string Name => "stranded-saga-sweep";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var wakes = new CoordinatorWakeLookup(services.GetService<IWorkCoordinator>());
    foreach (var participant in services.GetServices<ISagaWatchdogParticipant>()) {
      try {
        var armed = await participant.ArmStrandedSagasAsync(wakes, cancellationToken).ConfigureAwait(false);
        if (armed > 0) {
          LogArmed(logger, armed, participant.SagaName);
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogSweepFailed(logger, participant.SagaName, ex);
      }
    }
  }

  /// <summary>Answers from the work coordinator's outbox and inbox; unknown when there is none.</summary>
  private sealed class CoordinatorWakeLookup(IWorkCoordinator? coordinator) : ISagaWakeLookup {
    public Task<IReadOnlySet<Guid>?> WithPendingWakeAsync(IReadOnlyList<Guid> sagaIds, CancellationToken cancellationToken)
      => coordinator is null
        ? Task.FromResult<IReadOnlySet<Guid>?>(null)
        : coordinator.GetStreamsWithPendingMessagesAsync(sagaIds, _tickTypeNames, cancellationToken);
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Re-armed the completion watchdog of {Count} stranded {SagaName} saga(s) whose chain had ended")]
  private static partial void LogArmed(ILogger logger, int count, string sagaName);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Stranded-saga sweep for {SagaName} failed; the other sagas were still swept and this one retries next cycle")]
  private static partial void LogSweepFailed(ILogger logger, string sagaName, Exception exception);
}
