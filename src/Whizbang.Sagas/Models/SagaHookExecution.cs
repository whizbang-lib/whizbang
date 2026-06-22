namespace Whizbang.Sagas.Models;

/// <summary>
/// Per-hook execution record on <see cref="BaseSagaModel.Hooks"/>.
/// Mirrors <see cref="SagaItemState"/> semantics so Rule-17 saga
/// completion can treat unfinished hooks as in-flight work alongside
/// unfinished items.
/// </summary>
/// <remarks>
/// Consumers never construct these directly. The framework's
/// <c>BaseSagaService.TryRunHookAsync</c> declares hooks via the
/// <c>SagaInitiatedEvent.HookNames</c> list and transitions each row
/// through Pending → Running → Completed/Failed as the bookend events
/// fire.
/// </remarks>
public class SagaHookExecution {

  /// <summary>Caller-defined hook name.</summary>
  public string HookName { get; set; } = string.Empty;

  /// <summary>Optional human-readable display name for UI.</summary>
  public string? DisplayName { get; set; }

  /// <summary>Current state — uses <see cref="SagaItemState"/> so item and hook lifecycles share one enum.</summary>
  public SagaItemState Status { get; set; } = SagaItemState.Pending;

  /// <summary>Wall-clock timestamp when the hook moved to <see cref="SagaItemState.Running"/>.</summary>
  public DateTimeOffset? StartedAt { get; set; }

  /// <summary>Wall-clock timestamp when the hook reached a terminal state.</summary>
  public DateTimeOffset? CompletedAt { get; set; }

  /// <summary>Short human-readable failure reason.</summary>
  public string? ErrorMessage { get; set; }

  /// <summary>Optional structured error details.</summary>
  public string? ErrorDetails { get; set; }

  /// <summary>True once the hook is in a terminal state.</summary>
  public bool IsTerminal => Status is SagaItemState.Completed or SagaItemState.Failed or SagaItemState.Skipped;
}
